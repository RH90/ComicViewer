using System;
using System.Collections.Generic;
using System.Text;

namespace ComicViewer.Objects
{
    class JsonComic
    {
        private int windowWidth = 1000;
        private int windowHeight = 1000;
        private int windowX = 100;
        private double fixedImageWidth = 0.8;
        private double fixedImageRatio = 1.0;
        private int scrollUpdateInterval = 30;
        private string dbPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\" + "comics.db";
        //private Dictionary<String, ComicItem> list = new Dictionary<String, ComicItem>();
        //private int listCount = 0;
        //private List<ComicItem> list = new List<ComicItem>();



        public int WindowWidth { get => windowWidth; set => windowWidth = value; }
        public int WindowX { get => windowX; set => windowX = value; }
        public int WindowHeight { get => windowHeight; set => windowHeight = value; }
        public double FixedImageWidth { get => fixedImageWidth; set => fixedImageWidth = value; }
        public double FixedImageRatio { get => fixedImageRatio; set => fixedImageRatio = value; }
        public int ScrollUpdateInterval { get => scrollUpdateInterval; set => scrollUpdateInterval = value; }
        public string DbPath { get => dbPath; set => dbPath = value; }
        //public int ListCount { get => List.Count; set => listCount = value; }
        //public Dictionary<String, ComicItem> List { get => list; set => list = value; }

        //public List<ComicItem> List { get => list; set => list = value; }

    }
}

