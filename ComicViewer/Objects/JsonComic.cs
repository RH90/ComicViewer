using System;
using System.Collections.Generic;
using System.Text;

namespace ComicViewer.Objects
{
    class JsonComic
    {
        private List<ComicItem> list = new List<ComicItem>();
        private int windowWidth = 1000;
        private int windowHeight = 1000;
        private int windowX = 100;

        public List<ComicItem> List { get => list; set => list = value; }
        public int WindowWidth { get => windowWidth; set => windowWidth = value; }
        public int WindowX { get => windowX; set => windowX = value; }
        public int WindowHeight { get => windowHeight; set => windowHeight = value; }
    }
}

