using System;
using Config;
using Vector;
using CNCState;

namespace Actions
{
    public class RTArcMoveCommand : IRTMoveCommand
    {
        public bool CommandIsCached { get { return true; } }
        
        private MachineParameters Config;
        private Vector2 DeltaProj;
        private decimal Height;
        private Vector2 startToCenterProj;
        private Vector2 endToCenterProj;
        private bool bigArc;
        private decimal LengthProj;

        public AxisState.Plane Plane { get; private set; }
        public RTMovementOptions Options { get; private set; }
        public Vector3 Delta { get; private set; }
        public Vector3 PhysicalDelta { get; private set; }
        public bool CCW { get; private set; }
        public decimal R { get; private set; }

        public Vector3 DirStart { get; private set; }
        public Vector3 DirEnd { get; private set; }
        public decimal Length { get; private set; }
        public decimal Angle { get; private set; }

        private readonly double eps = 1e-6;

        private bool left_basis
        {
            get
            {
                bool left = false;
                switch (Plane)
                {
                    case AxisState.Plane.XY:
                        if (Config.X_axis.invert)
                            left = !left;
                        if (Config.Y_axis.invert)
                            left = !left;
                        return left;
                    case AxisState.Plane.YZ:
                        if (Config.Y_axis.invert)
                            left = !left;
                        if (Config.Z_axis.invert)
                            left = !left;
                        return left;
                    case AxisState.Plane.ZX:
                        if (Config.Z_axis.invert)
                            left = !left;
                        if (Config.Z_axis.invert)
                            left = !left;
                        return left;
                    default:
                        throw new ArgumentOutOfRangeException("Invalid arc plane selection");
                }   
            }
        }

        private String plane_cmd
        {
            get
            {
                switch (Plane)
                {
                    case AxisState.Plane.XY:
                        return "G17";
                    case AxisState.Plane.YZ:
                        return "G18";
                    case AxisState.Plane.ZX:
                        return "G19";
                    default:
                        throw new ArgumentOutOfRangeException("Invalid arc plane selection");
                }
            }
        }

        private String MoveCmd
        {
            get
            {
                bool hwccw;
                if (left_basis)
                    hwccw = !CCW;
                else
                    hwccw = CCW;

                if (hwccw)
                    return "G3";
                else
                    return "G2";            
            }
        }

        private int dx, dy, dz;
        private decimal a, b;
        private int x1, y1;
        private int x2, y2;
        private int h;

        public String Command
        {
            get
            {   
                return $"{MoveCmd} {plane_cmd} {Options.Command} X{x2}Y{y2}R{x1}S{y1}H{h}D{Length:0.000} A{a:0.000}B{b:0.000}";
            }
        }

        private (Vector2, Vector2) FindTangents(Vector2 startToCenter, Vector2 endToCenter, bool ccw)
        {
            var st = startToCenter.Right();
            var et = endToCenter.Right();
            if (!ccw)
            {
                st = -st;
                et = -et;
            }
            return (st / st.Length(), et / et.Length());
        }

        private Vector2 VectorPlaneProj(Vector3 delta, AxisState.Plane plane)
        {
            switch (plane)
            {
                case AxisState.Plane.XY:
                    return new Vector2(delta.x, delta.y);
                case AxisState.Plane.YZ:
                    return new Vector2(delta.y, delta.z);
                case AxisState.Plane.ZX:
                    return new Vector2(delta.z, delta.x);
                default:
                    throw new ArgumentOutOfRangeException("Invalid axis");
            }
        }

        private decimal VectorPlaneNormalProj(Vector3 delta, AxisState.Plane plane)
        {
            switch (plane)
            {
                case AxisState.Plane.XY:
                    return delta.z;
                case AxisState.Plane.YZ:
                    return delta.x;
                case AxisState.Plane.ZX:
                    return delta.y;
                default:
                    throw new ArgumentOutOfRangeException("Invalid axis");
            }
        }

        private void FillDirs()
        {
            var tans = FindTangents(startToCenterProj, endToCenterProj, CCW);
            Vector2 startTan = tans.Item1;
            Vector2 endTan = tans.Item2;

            decimal verticalTan = Height / LengthProj;

            switch (Plane)
            {
                case AxisState.Plane.XY:
                    DirStart = new Vector3(startTan.x, startTan.y, verticalTan);
                    DirEnd = new Vector3(endTan.x, endTan.y, verticalTan);
                    break;
                case AxisState.Plane.YZ:
                    DirStart = new Vector3(verticalTan, startTan.x, startTan.y);
                    DirEnd = new Vector3(verticalTan, endTan.x, endTan.y);
                    break;
                case AxisState.Plane.ZX:
                    DirStart = new Vector3(startTan.y, verticalTan, startTan.x);
                    DirEnd = new Vector3(endTan.y, verticalTan, endTan.x);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("Invalid axis");
            }
            DirStart = Vector3.Normalize(DirStart);
            DirEnd = Vector3.Normalize(DirEnd);
        }

        private void FindPhysicalParameters()
        {
            Vector3 hwdelta = new Vector3
            {
                x = Delta.x * Config.X_axis.sign,
                y = Delta.y * Config.Y_axis.sign,
                z = Delta.z * Config.Z_axis.sign
            };

            dx = (int)(hwdelta.x * Config.X_axis.steps_per_mm);
            dy = (int)(hwdelta.y * Config.Y_axis.steps_per_mm);
            dz = (int)(hwdelta.z * Config.Z_axis.steps_per_mm);

            switch (Plane)
            {
                case AxisState.Plane.XY:
                    x1 = (int)(Math.Round(-startToCenterProj.x * Config.X_axis.steps_per_mm * Config.X_axis.sign));
                    y1 = (int)(Math.Round(-startToCenterProj.y * Config.Y_axis.steps_per_mm * Config.Y_axis.sign));
                    x2 = (int)(Math.Round(-endToCenterProj.x * Config.X_axis.steps_per_mm * Config.X_axis.sign));
                    y2 = (int)(Math.Round(-endToCenterProj.y * Config.Y_axis.steps_per_mm * Config.Y_axis.sign));
                    h = (int)(Math.Round(Height * Config.Z_axis.steps_per_mm * Config.Z_axis.sign));
                    break;
                case AxisState.Plane.YZ:
                    x1 = (int)(Math.Round(-startToCenterProj.x * Config.Y_axis.steps_per_mm * Config.Y_axis.sign));
                    y1 = (int)(Math.Round(-startToCenterProj.y * Config.Z_axis.steps_per_mm * Config.Z_axis.sign));
                    x2 = (int)(Math.Round(-endToCenterProj.x * Config.Y_axis.steps_per_mm * Config.Y_axis.sign));
                    y2 = (int)(Math.Round(-endToCenterProj.y * Config.Z_axis.steps_per_mm * Config.Z_axis.sign));
                    h = (int)(Math.Round(Height * Config.X_axis.steps_per_mm));
                    break;
                case AxisState.Plane.ZX:
                    x1 = (int)(Math.Round(-startToCenterProj.x * Config.Z_axis.steps_per_mm * Config.Z_axis.sign));
                    y1 = (int)(Math.Round(-startToCenterProj.y * Config.X_axis.steps_per_mm * Config.X_axis.sign));
                    x2 = (int)(Math.Round(-endToCenterProj.x * Config.Z_axis.steps_per_mm * Config.Z_axis.sign));
                    y2 = (int)(Math.Round(-endToCenterProj.y * Config.X_axis.steps_per_mm * Config.X_axis.sign));
                    h = (int)(Math.Round(Height * Config.Y_axis.steps_per_mm * Config.Y_axis.sign));
                    break;
            }

            bool hwccw;
            if (left_basis)
            {
                hwccw = !CCW;
            }
            else
            {
                hwccw = CCW;
            }

            switch (Plane)
            {
                case AxisState.Plane.XY:
                    a = R * Config.X_axis.steps_per_mm;
                    b = R * Config.Y_axis.steps_per_mm;
                    break;
                case AxisState.Plane.YZ:
                    a = R * Config.Y_axis.steps_per_mm;
                    b = R * Config.Z_axis.steps_per_mm;
                    break;
                case AxisState.Plane.ZX:
                    a = R * Config.Z_axis.steps_per_mm;
                    b = R * Config.X_axis.steps_per_mm;
                    break;
                default:
                    throw new InvalidOperationException("Invalid plane");
            }

            if (bigArc)
            {
                a = -a;
                b = -b;
            }

            PhysicalDelta = new Vector3
            {
                x = dx / Config.X_axis.steps_per_mm * Config.X_axis.sign,
                y = dy / Config.Y_axis.steps_per_mm * Config.Y_axis.sign,
                z = dz / Config.Z_axis.steps_per_mm * Config.Z_axis.sign
            };

        }

        private void Setup(Vector3 delta, bool ccw, AxisState.Plane plane, RTMovementOptions opts, MachineParameters config)
        {
            Plane = plane;
            Delta = delta;
            CCW = ccw;
            Config = config;
            Options = opts;
            DeltaProj = VectorPlaneProj(Delta, Plane);
            Height = VectorPlaneNormalProj(delta, Plane);
        }

        private void FinalSetup()
        {
            Angle = (decimal)(2 * Math.Asin((double)(DeltaProj.Length() / 2 / R)));
            if (bigArc)
            {
                Angle = (decimal)(Math.PI * 2) - Angle;
            }
            LengthProj = Angle * R;
            Length = (decimal)Math.Sqrt((double)(LengthProj*LengthProj + Height*Height));

            FillDirs();
            FindPhysicalParameters();
        }

        public RTArcMoveCommand(Vector3 delta, decimal r, bool ccw, AxisState.Plane plane,
                                RTMovementOptions opts, MachineParameters config)
        {
            Setup(delta, ccw, plane, opts, config);

            R = Math.Abs(r);
            bigArc = (r < 0);
            decimal d = DeltaProj.Length();
            decimal hcl = (decimal)Math.Sqrt((double)(R*R - d*d/4));
            if (ccw && !bigArc || !ccw && bigArc)
                hcl = -hcl;
            startToCenterProj = DeltaProj / 2 + Vector2.Normalize(DeltaProj.Right()) * hcl;
            endToCenterProj = startToCenterProj - DeltaProj;

            FinalSetup();
        }

        public RTArcMoveCommand(Vector3 delta, Vector3 startToCenter, bool ccw, AxisState.Plane plane,
                                RTMovementOptions opts, MachineParameters config)
        {
            Setup(delta, ccw, plane, opts, config);

            startToCenterProj = VectorPlaneProj(startToCenter, Plane);
            endToCenterProj = startToCenterProj - DeltaProj;
            decimal nd = Vector2.Cross(DeltaProj, startToCenterProj);
            if (nd > 0 && CCW || nd < 0 && !CCW)
                bigArc = false;
            else
                bigArc = true;
            R = startToCenter.Length();

            FinalSetup();
        }
    }
}
