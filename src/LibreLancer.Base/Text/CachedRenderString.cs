// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;

namespace LibreLancer
{
    public abstract class CachedRenderString
    {
        internal string FontName;
        internal string Text;
        internal float FontSize;
        internal bool Underline;
        internal TextAlignment Alignment;


        //Bail out on long strings
        static bool FastCompare(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length || a.Length > 32) return false;
            return a == b;
        }
        //Returns true if the text must be updated
        internal bool Update(
            string font, 
            string text, float size, 
            bool underline, TextAlignment alignment
        )
        {
            if (!FastCompare(font, FontName) ||
                !FastCompare(text, Text) ||
                Math.Abs(size - FontSize) > float.Epsilon ||
                underline != Underline ||
                Alignment != alignment)
            {
                FontName = font;
                Text = text;
                FontSize = size;
                Underline = underline;
                Alignment = alignment;
                return true;
            }
            return false;
        }
    }
}