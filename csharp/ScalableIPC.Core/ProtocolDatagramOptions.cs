﻿using System;
using System.Collections.Generic;
using System.Text;

namespace ScalableIPC.Core
{
    public class ProtocolDatagramOptions
    {
        // Reserve s_ prefix for standard options at session layer.
        public const string StandardSessionLayerOptionPrefix = "s_";

        // Reserve sx_ prefix for non-standard options at session layer.
        public const string NonStandardSessionLayerOptionPrefix = "sx_";

        public const string OptionNameIdleTimeout = StandardSessionLayerOptionPrefix + "idle_timeout";
        public const string OptionNameErrorCode = StandardSessionLayerOptionPrefix + "err_code";
        public const string OptionNameIsWindowFull = StandardSessionLayerOptionPrefix + "window_full";
        public const string OptionNameIsLastInWindow = StandardSessionLayerOptionPrefix + "last_in_window";

        public Dictionary<string, List<string>> AllOptions { get; } = new Dictionary<string, List<string>>();

        // Standard session layer options.
        public int? IdleTimeoutSecs { get; set; }
        public int? ErrorCode { get; set; }
        public bool? IsWindowFull { get; set; }
        public bool? IsLastInWindow { get; set; }

        public void AddOption(string name, string value)
        {
            List<string> optionValues;
            if (AllOptions.ContainsKey(name))
            {
                optionValues = AllOptions[name];
            }
            else
            {
                optionValues = new List<string>();
                AllOptions.Add(name, optionValues);
            }
            optionValues.Add(value);

            // Now identify and validate standard options.
            // In case of repetition, last one wins.
            switch (name)
            {
                case OptionNameIdleTimeout:
                    IdleTimeoutSecs = ParseOptionAsInt32(value);
                    break;
                case OptionNameErrorCode:
                    ErrorCode = ParseOptionAsInt32(value);
                    break;
                case OptionNameIsLastInWindow:
                    IsLastInWindow = ParseOptionAsBoolean(value);
                    break;
                case OptionNameIsWindowFull:
                    IsWindowFull = ParseOptionAsBoolean(value);
                    break;
                default:
                    break;
            }
        }

        internal static int ParseOptionAsInt32(string optionValue)
        {
            return int.Parse(optionValue);
        }

        internal static bool ParseOptionAsBoolean(string optionValue)
        {
            switch (optionValue.ToLowerInvariant())
            {
                case "true":
                    return true;
                case "false":
                    return false;
            }
            throw new Exception($"expected {true} or {false}");
        }

        public IEnumerable<string[]> GenerateList()
        {
            var knownOptions = GatherKnownOptions();

            // store usages of known options when iterating over AllOptions.
            // will need it in the end
            var knownOptionsUsed = new HashSet<string>();
            
            foreach (var kvp in AllOptions)
            {
                string lastValue = null;
                foreach (var optionValue in kvp.Value)
                {
                    yield return new string[] { kvp.Key, optionValue };
                    lastValue = optionValue;
                }

                // override with known options if defined differently from last value in
                // AllOptions.
                if (knownOptions.ContainsKey(kvp.Key))
                {
                    string overridingValue = knownOptions[kvp.Key];
                    // only send out known value if it is different from the last value in
                    // AllOptions to avoid unnecessary duplication.
                    if (lastValue == null || lastValue != overridingValue)
                    {
                        yield return new string[] { kvp.Key, overridingValue };
                        knownOptionsUsed.Add(kvp.Key);
                    }
                }
            }

            // Just in case AllOptions doesn't contain a known option,
            // deal with that possibility here.
            foreach (var kvp in knownOptions)
            {
                if (!knownOptionsUsed.Contains(kvp.Key))
                {
                    yield return new string[] { kvp.Key, kvp.Value };
                }
            }
        }

        private Dictionary<string, string> GatherKnownOptions()
        {
            var knownOptions = new Dictionary<string, string>();
            if (IdleTimeoutSecs != null)
            {
                knownOptions.Add(OptionNameIdleTimeout, IdleTimeoutSecs.ToString());
            }
            if (ErrorCode != null)
            {
                knownOptions.Add(OptionNameErrorCode, ErrorCode.ToString());
            }
            if (IsLastInWindow != null)
            {
                knownOptions.Add(OptionNameIsLastInWindow, IsLastInWindow.ToString());
            }
            if (IsWindowFull != null)
            {
                knownOptions.Add(OptionNameIsWindowFull, IsWindowFull.ToString());
            }
            return knownOptions;
        }
    }
}
