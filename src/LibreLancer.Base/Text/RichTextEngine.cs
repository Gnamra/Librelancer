// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Collections.Generic;
namespace LibreLancer
{
    public abstract class RichTextEngine : IDisposable
    {
        public abstract void Dispose();
        public abstract void RenderText(BuiltRichText txt, int x, int y);
        public abstract BuiltRichText BuildText(IList<RichTextNode> nodes, int width, float sizeMultiplier = 1f);
        public abstract void DrawStringBaseline(string fontName, float size, string text, float x, float y, Color4 color, bool underline = false, TextShadow shadow = default);
        public abstract Point MeasureString(string fontName, float size, string text);
        public abstract float LineHeight(string fontName, float size);
        public abstract void DrawStringCached(ref CachedRenderString cache, string fontName, float size, string text,
            float x, float y, Color4 color, bool underline = false, TextShadow shadow = default, TextAlignment alignment = TextAlignment.Left);
        public abstract Point MeasureStringCached(ref CachedRenderString cache, string fontName, float size, string text,
            bool underline, TextAlignment alignment);
    }
    
    public abstract class BuiltRichText : IDisposable
    {
        public abstract void Recalculate(float width);
        public abstract float Height { get; }
        public abstract void Dispose();
    }
}
