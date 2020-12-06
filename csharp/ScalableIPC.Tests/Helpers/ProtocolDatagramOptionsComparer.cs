﻿using ScalableIPC.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace ScalableIPC.Tests.Helpers
{
    public class ProtocolDatagramOptionsComparer: IEqualityComparer<ProtocolDatagramOptions>
    {
        public static readonly ProtocolDatagramOptionsComparer Default = new ProtocolDatagramOptionsComparer(false);

        private readonly bool _reverse;
        public ProtocolDatagramOptionsComparer(bool reverse)
        {
            _reverse = reverse;
        }

        public bool Equals(ProtocolDatagramOptions x, ProtocolDatagramOptions y)
        {
            bool result = _Equals(x, y);
            if (_reverse)
            {
                result = !result;
            }
            return result;
        }

        private bool _Equals(ProtocolDatagramOptions x, ProtocolDatagramOptions y)
        {
            if (x == y)
            {
                return true;
            }
            if (x == null || y == null)
            {
                return false;
            }
            if (x.IdleTimeoutSecs != y.IdleTimeoutSecs)
            {
                return false;
            }
            if (x.AbortCode != y.AbortCode)
            {
                return false;
            }
            if (x.IsWindowFull != y.IsWindowFull)
            {
                return false;
            }
            if (x.IsLastInWindow != y.IsLastInWindow)
            {
                return false;
            }
            if (x.IsLastInWindowGroup != y.IsLastInWindowGroup)
            {
                return false;
            }
            if (x.TraceId != y.TraceId)
            {
                return false;
            }
            if (!OptionsComparer.Default.Equals(x.AllOptions, y.AllOptions))
            {
                return false;
            }
            return true;
        }

        public int GetHashCode(ProtocolDatagramOptions obj)
        {
            return 1;
        }
    }
}