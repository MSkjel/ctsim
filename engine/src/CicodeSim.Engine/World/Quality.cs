using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The 64-bit QUALITY value and its named qualities.</summary>
    public static class Quality
    {
        public const int Bad = 0,
            Uncertain = 1,
            Good = 3;

        public const int BadNonSpecific = 0,
            BadConfigurationError = 1,
            BadNotConnected = 2,
            BadDeviceFailure = 3,
            BadSensorFailure = 4,
            BadLastKnownValue = 5,
            BadCommFailure = 6,
            BadOutOfService = 7,
            BadWaitingForIniData = 8;
        public const int UncrNonSpecific = 0,
            UncrLastUsableValue = 1,
            UncrSensorNotAccurate = 4,
            UncrEngUnitExceeded = 5,
            UncrSubnormal = 6;
        public const int GoodNonSpecific = 0,
            GoodLocalOverride = 6;

        public const int LimitedNotSet = 0,
            LimitedLow = 1,
            LimitedHigh = 2,
            LimitedConstant = 3;

        public const int ExtNonSpecific = 0x00,
            ExtScheduledOffline = 0x01,
            ExtInvalidTag = 0x02,
            ExtInvalidData = 0x03,
            ExtSoftwareError = 0x04,
            ExtTooManyDevices = 0x05,
            ExtCommNoInit = 0x06,
            ExtCommBad = 0x07,
            ExtTagOutOfRange = 0x08,
            ExtWriteOnly = 0x09,
            ExtWriteProtected = 0x0A,
            ExtNoClusterSpecified = 0x0B,
            ExtClusterNotFound = 0x0C,
            ExtClusterDisabled = 0x0D,
            ExtSessionNotConnected = 0x0E,
            ExtTagResolveTimeout = 0x0F,
            ExtValueOutOfRange = 0x10,
            ExtCommDevBad = 0x11,
            ExtCommIoserverBad = 0x12,
            ExtStale = 0x13,
            ExtNotReplicated = 0x14;

        public const ulong FlagOverride = 0x01UL << 16; // QTS_OVERRIDE
        public const ulong FlagControlInhibit = 0x02UL << 16; // QTS_CONTROL_INHIBIT

        /// <summary>A quality from its parts (QualityCreate's layout, without its range errors).</summary>
        public static ulong Make(
            int general = Good,
            int substatus = 0,
            int limit = 0,
            int extended = 0,
            bool overrideFlag = false,
            bool controlInhibit = false,
            long error = 0
        )
        {
            ulong q = (ulong)(((general & 3) << 6) | ((substatus & 0xF) << 2) | (limit & 3));
            q |= (ulong)(extended & 0xFF) << 8;
            if (overrideFlag)
                q |= FlagOverride;
            if (controlInhibit)
                q |= FlagControlInhibit;
            return q | ((ulong)error & 0xFFFFFFFFUL) << 32;
        }

        public static readonly ulong QualityGood = Make(Good); // 0xC0
        public static readonly ulong QualityBad = Make(Bad); // 0x00
        public static readonly ulong QualityUncertain = Make(Uncertain); // 0x40
        public static readonly ulong QualityTagNotFound = Make(
            Bad,
            BadConfigurationError,
            extended: ExtInvalidTag
        );
        public static readonly ulong QualityCommFailure = Make(Bad, BadCommFailure);
        public static readonly ulong QualityWaiting = Make(Bad, BadWaitingForIniData);

        public static int General(ulong q) => (int)((q & 0xFF) >> 6);

        public static int Substatus(ulong q) => (int)(((q & 0xFF) >> 2) & 0xF);

        public static int Limit(ulong q) => (int)(q & 3);

        public static int Extended(ulong q) => (int)((q >> 8) & 0xFF);

        /// <summary>QualityGetPart(q, 7): the OPC quality byte.</summary>
        public static int Opc(ulong q) => (int)(q & 0xFF);

        public static long ErrorCode(ulong q) => (long)((q >> 32) & 0xFFFFFFFFUL);

        public static bool IsGood(ulong q) => (q & 0xC0) == 0xC0;

        public static bool IsBad(ulong q) => (q & 0xFF) < 0x40;

        public static bool IsUncertain(ulong q) => (q & 0xC0) == 0x40;

        public static bool IsOverride(ulong q) => (q & FlagOverride) != 0;

        public static bool IsControlInhibit(ulong q) => (q & FlagControlInhibit) != 0;

        /// <summary>The quality with the override / control inhibit flags set or cleared (null =
        /// unchanged).</summary>
        public static ulong WithFlags(ulong q, bool? overrideFlag = null, bool? controlInhibit = null)
        {
            if (overrideFlag != null)
                q = overrideFlag.Value ? q | FlagOverride : q & ~FlagOverride;
            if (controlInhibit != null)
                q = controlInhibit.Value ? q | FlagControlInhibit : q & ~FlagControlInhibit;
            return q;
        }

        private static readonly List<KeyValuePair<string, ulong>> Names = new List<
            KeyValuePair<string, ulong>
        >
        {
            new("good", QualityGood),
            new("bad", QualityBad),
            new("uncertain", QualityUncertain),
            new("uncr", QualityUncertain),
            new("bad_config", Make(Bad, BadConfigurationError)),
            new("bad_configuration_error", Make(Bad, BadConfigurationError)),
            new("bad_not_connected", Make(Bad, BadNotConnected)),
            new("bad_device_failure", Make(Bad, BadDeviceFailure)),
            new("bad_sensor_failure", Make(Bad, BadSensorFailure)),
            new("bad_last_known_value", Make(Bad, BadLastKnownValue)),
            new("bad_comm_failure", QualityCommFailure),
            new("bad_comm", QualityCommFailure),
            new("bad_out_of_service", Make(Bad, BadOutOfService)),
            new("bad_waiting_for_initial_data", QualityWaiting),
            new("uncertain_last_usable_value", Make(Uncertain, UncrLastUsableValue)),
            new("uncertain_sensor_not_accurate", Make(Uncertain, UncrSensorNotAccurate)),
            new("uncertain_eng_unit_exceeded", Make(Uncertain, UncrEngUnitExceeded)),
            new("uncertain_subnormal", Make(Uncertain, UncrSubnormal)),
            new("good_local_override", Make(Good, GoodLocalOverride)),
        };

        /// <summary>A quality from a scenario / viewer: a name ('good', 'bad', 'uncertain',
        /// 'bad_comm_failure' ...; any case, '-' or a space for '_', an optional 'qual_' prefix), an
        /// unsigned integer ('0x18', '192'; its low 64 bits) or null (good). FormatException otherwise.</summary>
        public static ulong Parse(string? text)
        {
            if (text == null)
                return QualityGood;
            string key = CitectText.Lower(CitectText.Trim(text)).Replace("-", "_").Replace(" ", "_");
            if (key.StartsWith("qual_", StringComparison.Ordinal))
                key = key.Substring(5);
            foreach (var kv in Names)
                if (kv.Key == key)
                    return kv.Value;
            var v = ParseNumber(key);
            if (v != null)
                return v.Value;
            throw new FormatException($"not a quality: {CitectText.Quote(text)}");
        }

        /// <summary>A quality from a JSON scenario value (string, integer or null).</summary>
        public static ulong Parse(JsonValue? v)
        {
            if (v == null || v.IsNull)
                return QualityGood;
            if (v.Kind == JsonKind.Bool)
                throw new FormatException($"not a quality: {v.Dump()}");
            if (v.Kind == JsonKind.Int)
                return Mask64(v.BigValue);
            if (v.Kind == JsonKind.String)
                return Parse(v.Str);
            throw new FormatException($"not a quality: {v.Dump()}");
        }

        /// <summary>An unsigned integer written as text: decimal digits, or 0x and hex digits;
        /// its low 64 bits; null for anything else.</summary>
        private static ulong? ParseNumber(string key)
        {
            int i = 0,
                n = key.Length,
                bs = 10;
            if (n >= 2 && key[0] == '0' && (key[1] == 'x' || key[1] == 'X'))
            {
                bs = 16;
                i = 2;
            }
            if (i >= n)
                return null;
            var acc = System.Numerics.BigInteger.Zero;
            for (; i < n; i++)
            {
                char c = key[i];
                int d =
                    c >= '0' && c <= '9' ? c - '0'
                    : c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : c >= 'A' && c <= 'F' ? c - 'A' + 10
                    : 99;
                if (d >= bs)
                    return null;
                acc = acc * bs + d;
            }
            return Mask64(acc);
        }

        /// <summary>v &amp; 0xFFFFFFFFFFFFFFFF (two's complement for a negative v):
        /// a quality written as a number keeps its low 64 bits whatever its size.</summary>
        internal static ulong Mask64(System.Numerics.BigInteger v)
        {
            var mod = System.Numerics.BigInteger.One << 64;
            var m = v % mod;
            if (m.Sign < 0)
                m += mod;
            return (ulong)m;
        }

        /// <summary>A short name: 'good', 'bad', 'uncertain', or the most specific known name.</summary>
        public static string Name(ulong q)
        {
            foreach (var kv in Names)
                if (kv.Value == q)
                    return kv.Key;
            int g = General(q);
            return g == Good ? "good"
                : g == Uncertain ? "uncertain"
                : "bad";
        }
    }
}
