using System;
using System.Collections.Generic;
using System.Text;

namespace ComicViewer.Objects
{
    class ComicItem
    {
        private string name = "";
        private string parent = "";
        private int pos = 0;
        private bool fitToWindow = false;
        private long lastOpened = DateTime.Now.Ticks;

        public string Name { get => name; set => name = value; }
        public int Pos { get => pos; set => pos = value; }
        public bool FitToWindow { get => fitToWindow; set => fitToWindow = value; }
        public long LastOpened { get => lastOpened; set => lastOpened = value; }
        public string Parent { get => parent; set => parent = value; }
    }
}
