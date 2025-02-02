﻿using System;

namespace Rebus.Forklift.Common
{
    class KeyOption
    {
        public char KeyChar { get; private set; }
        public Action Action { get; private set; }
        public string Description { get; private set; }

        KeyOption(char keyChar, Action action, string description)
        {
            if (action == null) throw new ArgumentNullException("action");
            KeyChar = keyChar;
            Action = action;
            Description = description;
        }

        public static KeyOption New(char keyChar, Action action, string description, params object[] objs)
        {
            return new KeyOption(keyChar, action, string.Format(description, objs));
        }

        public override string ToString()
        {
            return string.Format("({0}) {1}", char.ToUpperInvariant(KeyChar), Description);
        }
    }
}