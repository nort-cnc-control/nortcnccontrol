﻿using System;
using System.Collections.Generic;
using Actions.ModbusTool;
using CNCState;

namespace Actions.Tools.SpindleTool
{
    public class N700ESpindleToolFactory : ISpindleToolFactory
    {
        private readonly UInt16 devid = 0x0001;

        private readonly UInt16 runRegister = 0x0002;
        private readonly UInt16 speedRegister = 0x0004;

        private readonly UInt16 runForward = 0x0001;
        private readonly UInt16 runReverse = 0x0002;
        private readonly UInt16 runNone = 0x0000;

        public ModbusToolCommand CreateSpindleToolCommand(SpindleState.SpindleRotationState rotation, decimal speed)
        {
            var registers = new ModbusRegister[2];
            int delay = 0;
            switch (rotation)
            {
                case SpindleState.SpindleRotationState.Off:
                    registers[0] = new ModbusRegister { DeviceId = devid, RegisterId = runRegister, RegisterValue = runNone };
                    delay = 0;
                    break;
                case SpindleState.SpindleRotationState.Clockwise:
                    registers[0] = new ModbusRegister { DeviceId = devid, RegisterId = runRegister, RegisterValue = runForward };
                    delay = 3000;
                    break;
                case SpindleState.SpindleRotationState.CounterClockwise:
                    registers[0] = new ModbusRegister { DeviceId = devid, RegisterId = runRegister, RegisterValue = runReverse };
                    delay = 3000;
                    break;
            }
            registers[1] = new ModbusRegister { DeviceId = devid, RegisterId = speedRegister, RegisterValue = (UInt16)(speed / 60.0m * 100) };
            return new ModbusToolCommand { Registers = registers, Delay = delay };
        }
    }
}
