﻿using System;

namespace Shared.Logging
{
    public class DebugLoggerFactory : ILoggerFactory
    {
        public ILog Create(Type type)
        {
            return new DebugLogger(type.Name);
        }
    }
}