﻿using System;
using System.Collections.Generic;
using Machine;
using Config;
using ActionProgram;
using Actions;
using Actions.Tools.SpindleTool;
using CNCState;
using RTSender;
using ModbusSender;
using ControlConnection;
using System.IO;
using System.Linq;
using GCodeMachine;
using System.Json;
using System.Threading;
using Actions.Tools;
using Log;
using System.Collections.Concurrent;
using Vector;

namespace GCodeServer
{

    public class GCodeServer : IDisposable, IMessageRouter, IStateSyncManager, ILoggerSource
    {
        public MachineParameters Config { get; private set; }
        public GCodeMachine.GCodeMachine Machine { get; private set; }
        public ReadStatusMachine.ReadStatusMachine StatusMachine { get; private set; }

        public string Name => "gcode server";

        private ProgramBuilder programBuilder;

        private readonly IRTSender rtSender;
        private readonly IModbusSender modbusSender;
        private readonly ISpindleToolFactory spindleToolFactory;

        private IReadOnlyDictionary<IAction, int> starts;

        private CNCState.CNCState State => Machine.LastState;
        private AxisState.CoordinateSystem hwCoordinateSystem;

        private MessageReceiver cmdReceiver;
        private MessageSender responseSender;

        private readonly Stream commandStream;
        private readonly Stream responseStream;

        private IToolManager toolManager;
        private bool serverRun;

        private BlockingCollection<JsonObject> commands;

        public GCodeServer(IRTSender rtSender,
                           IModbusSender modbusSender,
                           ISpindleToolFactory spindleToolFactory,
                           MachineParameters config,
                           Stream commandStream,
                           Stream responseStream)
        {
            this.rtSender = rtSender;
            this.modbusSender = modbusSender;
            this.spindleToolFactory = spindleToolFactory;
            this.Config = config;
            this.commandStream = commandStream;
            this.responseStream = responseStream;
            StatusMachine = new ReadStatusMachine.ReadStatusMachine(rtSender, Config.state_refresh_timeout);
            StatusMachine.CurrentStatusUpdate += OnStatusUpdate;
            commands = new BlockingCollection<JsonObject>();
            Init();

            cmdReceiver = new MessageReceiver(commandStream);
            responseSender = new MessageSender(responseStream);
        }

        private void Init()
        {
            var newState = new CNCState.CNCState(new AxisState(), new SpindleState(), new DrillingState());
            Machine = new GCodeMachine.GCodeMachine(this.rtSender, this, newState, Config);

            var crds = StatusMachine.ReadHardwareCoordinates();
            var sign = new Vector3(Config.SignX, Config.SignY, Config.SignZ);
            hwCoordinateSystem = new AxisState.CoordinateSystem
            {
                Sign = sign,
                Offset = new Vector3(crds.x - sign.x * State.AxisState.Position.x,
                                     crds.y - sign.y * State.AxisState.Position.y,
                                     crds.z - sign.z * State.AxisState.Position.z)
            };

            Machine.ActionStarted += Machine_ActionStarted;
            toolManager = new ManualToolManager(this, Machine);
            programBuilder = new ProgramBuilder(Machine,
                                                this,
                                                rtSender,
                                                modbusSender,
                                                spindleToolFactory,
                                                toolManager,
                                                Config);
        }

        public void Message(IReadOnlyDictionary<string, string> message)
        {
            var response = new JsonObject();
            foreach (var keyval in message)
            {
                response[keyval.Key] = keyval.Value;
            }
            responseSender.MessageSend(response.ToString());
        }

        private void OnStatusUpdate(Vector3 hw_crds, bool ex, bool ey, bool ez, bool ep)
        {
            var gl_crds = hwCoordinateSystem.ToLocal(hw_crds);
            var (loc_crds, crd_system) = Machine.ConvertCoordinates(gl_crds);
            var response = new JsonObject
            {
                ["type"] = "coordinates",
                ["hardware"] = new JsonArray
                    {
                        hw_crds.x,
                        hw_crds.y,
                        hw_crds.z
                    },
                ["global"] = new JsonArray
                    {
                        gl_crds.x,
                        gl_crds.y,
                        gl_crds.z
                    },
                ["local"] = new JsonArray
                    {
                        loc_crds.x,
                        loc_crds.y,
                        loc_crds.z
                    },
                ["cs"] = crd_system,
            };
            var resp = response.ToString();
            responseSender.MessageSend(resp);
        }

        private void Machine_ActionStarted(IAction action)
        {
            if (starts == null)
                return;
            try
            {
                int index = starts[action];

                var response = new JsonObject
                {
                    ["type"] = "line",
                    ["line"] = index
                };
                responseSender.MessageSend(response.ToString());
            }
            catch
            {
                ;
            }
        }


        #region gcode machine methods
        private void RunGcode(String[] prg)
        {
            ActionProgram.ActionProgram program;
            double time;
            try
            {
                (program, _, starts, time) = programBuilder.BuildProgram(prg, Machine.LastState);
            }
            catch (Exception e)
            {
                Logger.Instance.Error(this, "compile", String.Format("Exception: {0}", e));
                return;
            }
            Logger.Instance.Info(this, "compile", String.Format("Expected execution time = {0}", time));
            Machine.LoadProgram(program);
            Machine.Start();
        }
        #endregion

        private void ReceiveCmdCycle()
        {
            String[] gcodeprogram = { };
            cmdReceiver.Run();
            do
            {

                var cmd = cmdReceiver.MessageReceive();
                if (cmd == null)
                {
                    // broken connection
                    cmdReceiver.Stop();
                    StatusMachine.Stop();
                    Machine.Stop();
                    break;
                }
                JsonValue message;
                try
                {
                    message = JsonValue.Parse(cmd);
                }
                catch
                {
                    cmdReceiver.Stop();
                    StatusMachine.Stop();
                    Machine.Stop();
                    serverRun = false;
                    Logger.Instance.Error(this, "parse", cmd);
                    break;
                }

                string type = message["type"];
                switch (type)
                {
                    case "command":
                        {
                            string command = message["command"];
                            switch (command)
                            {
                                case "exit":
                                    {
                                        cmdReceiver.Stop();
                                        StatusMachine.Stop();
                                        Machine.Stop();
                                        serverRun = false;
                                        break;
                                    }
                                case "disconnect":
                                    {
                                        cmdReceiver.Stop();
                                        StatusMachine.Stop();
                                        Machine.Stop();
                                        serverRun = false;
                                        break;
                                    }
                                case "reboot":
                                    {
                                        Machine.Reboot();
                                        break;
                                    }
                                case "reset":
                                    {
                                        StatusMachine.Stop();
                                        Machine.Abort();
                                        Machine.ActionStarted -= Machine_ActionStarted;
                                        Machine.Dispose();
                                        Init();
                                        StatusMachine.Start();
                                        break;
                                    }
                                case "stop":
                                    {
                                        Machine.Stop();
                                        break;
                                    }
                                case "pause":
                                    {
                                        break;
                                    }
                                case "load":
                                    {

                                        List<string> program = new List<string>();
                                        foreach (JsonPrimitive line in message["program"])
                                        {
                                            string str = line;
                                            program.Add(str);
                                        }
                                        gcodeprogram = program.ToArray();
                                        var response = new JsonObject
                                        {
                                            ["type"] = "loadlines",
                                            ["lines"] = message["program"]
                                        };
                                        responseSender.MessageSend(response.ToString());
                                        break;
                                    }
                                case "continue":
                                    {
                                        commands.Add(new JsonObject
                                            {
                                                ["command"] = "continue",
                                            }
                                        );
                                        break;
                                    }
                                case "start":
                                    {
                                        var lines = gcodeprogram.Select(line => new JsonPrimitive(line));
                                        commands.Add(new JsonObject
                                            {
                                                ["command"] = "start",
                                                ["program"] = new JsonArray(lines),
                                            }
                                        );
                                        break;
                                    }
                                case "execute":
                                    {
                                        var lines = new List<JsonValue>
                                        {
                                            message["program"]
                                        };

                                        commands.Add(new JsonObject
                                            {
                                                ["command"] = "start",
                                                ["program"] = new JsonArray(lines),
                                            }
                                        );
                                        break;
                                    }
                                default:
                                    throw new ArgumentException(String.Format("Invalid command \"{0}\"", message.ToString()));
                            }
                            break;
                        }
                }

            } while (serverRun);
            cmdReceiver.Stop();
        }

        public bool Run()
        {
            StatusMachine.Start();
            serverRun = true;
            var cmdThread = new Thread(new ThreadStart(ReceiveCmdCycle));
            cmdThread.Start();
            String[] gcodeprogram = { };
            do
            {
                var command = commands.Take();
                string cmd = command["command"];
                switch (cmd)
                {
                    case "start":
                        {
                            List<string> program = new List<string>();
                            foreach (JsonPrimitive line in command["program"])
                            {
                                string str = line;
                                program.Add(str);
                            }
                            RunGcode(program.ToArray());
                            break;
                        }
                    case "continue":
                        {
                            Machine.Continue();
                            break;
                        }
                }

            } while (true);
        }

        public void Dispose()
        {
            if (cmdReceiver != null)
            {
                cmdReceiver.Dispose();
                cmdReceiver = null;
            }
            if (responseSender != null)
            {
                responseSender.Dispose();
                responseSender = null;
            }
            Machine.Dispose();
            StatusMachine.Dispose();
        }

        public void SyncCoordinates(Vector3 stateCoordinates)
        {
            var crds = StatusMachine.ReadHardwareCoordinates();
            var sign = new Vector3(Config.SignX, Config.SignY, Config.SignZ);
            hwCoordinateSystem = new AxisState.CoordinateSystem
            {
                Sign = sign,
                Offset = new Vector3(crds.x - sign.x * State.AxisState.Position.x,
                                     crds.y - sign.y * State.AxisState.Position.y,
                                     crds.z - sign.z * State.AxisState.Position.z)
            };
        }
    }
}
