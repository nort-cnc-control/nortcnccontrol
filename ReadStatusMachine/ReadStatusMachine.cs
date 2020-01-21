﻿using System;
using System.Threading;
using Machine;
using Actions;
using RTSender;
using System.Globalization;

namespace ReadStatusMachine
{
    public class ReadStatusMachine : IMachine
    {
        private bool run;
        private readonly IRTSender rtSender;
        private Thread askPosThread;
        private readonly int timeout;
        private EventWaitHandle timeoutWait;

        public event Action<Vector3, bool, bool, bool, bool> CurrentStatusUpdate;

        public ReadStatusMachine(IRTSender rtSender, int updateT)
        {
            this.rtSender = rtSender;
            timeout = updateT;
            timeoutWait = new EventWaitHandle(false, EventResetMode.AutoReset);
        }

        public State RunState { get; private set; }

        public void Abort()
        {
            run = false;
            timeoutWait.Set();
            askPosThread.Join();
        }

        public void Activate()
        {
        }

        public void Continue()
        {
            run = true;
            askPosThread = new Thread(new ThreadStart(AskPosition));
            askPosThread.Start();
        }

        public void Dispose()
        {
            run = false;
            timeoutWait.Set();
            askPosThread.Join();
        }

        public void Pause()
        {
            run = false;
            timeoutWait.Set();
            askPosThread.Join();
        }

        public void Reboot()
        {
        }

        public Vector3 ReadHardwareCoordinates()
        {
            while (true)
            {
                try
                {
                    RTAction action = new RTAction(rtSender, new RTGetPositionCommand());
                    // action.ReadyToRun.WaitOne();
                    action.Run();
                    action.Finished.WaitOne(500);
                    var xs = action.ActionResult["X"];
                    var ys = action.ActionResult["Y"];
                    var zs = action.ActionResult["Z"];
                    return new Vector3(double.Parse(xs, CultureInfo.InvariantCulture),
                                       double.Parse(ys, CultureInfo.InvariantCulture),
                                       double.Parse(zs, CultureInfo.InvariantCulture));
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }

        public (bool ex, bool ey, bool ez, bool ep) ReadCurrentEndstops()
        {
            RTAction action = new RTAction(rtSender, new RTGetEndstopsCommand());
            // action.ReadyToRun.WaitOne();
            action.Run();
            action.Finished.WaitOne();
            return (action.ActionResult["EX"] == "1",
                    action.ActionResult["EY"] == "1",
                    action.ActionResult["EZ"] == "1",
                    action.ActionResult["EP"] == "1");
        }

        private void AskPosition()
        {
            while (run)
            {
                try
                {
                    var hw_crds = ReadHardwareCoordinates();
                    //var (ex, ey, ez, ep) = ReadCurrentEndstops();
                    var (ex, ey, ez, ep) = (false, false, false, false);
                    CurrentStatusUpdate?.Invoke(hw_crds, ex, ey, ez, ep);
                }
                catch
                {
                    ;
                }
                timeoutWait.WaitOne(timeout);
            }
            //Console.WriteLine("End ask coordinate");
        }

        public void Start()
        {
            Continue();
        }

        public void Stop()
        {
            run = false;
            askPosThread.Join();
        }
    }
}
