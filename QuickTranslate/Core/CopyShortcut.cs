using System;
using System.Collections.Generic;

namespace QuickTranslate.Core
{
    public sealed record CopyShortcut(byte Key, bool Ctrl, bool Alt, bool Shift)
    {
        public static CopyShortcut CtrlC { get; } = new(0x43, true, false, false);
        public static CopyShortcut CtrlShiftC { get; } = new(0x43, true, false, true);

        public static bool TryParse(string? value, out CopyShortcut shortcut)
        {
            shortcut = CtrlC;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var ctrl = false; var alt = false; var shift = false; byte? key = null;
            foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = rawPart.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = (byte)char.ToUpperInvariant(part[0]);
                else return false;
            }
            if (key == null || (!ctrl && !alt && !shift)) return false;
            shortcut = new CopyShortcut(key.Value, ctrl, alt, shift);
            return true;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(((char)Key).ToString());
            return string.Join("+", parts);
        }
    }
}
