using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media.Imaging;

namespace ComicViewer.Objects
{
    internal class ImageContainer
    {
        private BitmapSource resizedImage = null;
        private int originalWidth = 0;
        private int originalHeight = 0;


        public ImageContainer(BitmapSource resizedImage, int originalWidth, int originalHeight)
        {
            this.resizedImage = resizedImage;
            this.originalWidth = originalWidth;
            this.originalHeight = originalHeight;
        }

        public BitmapSource ResizedImage { get => resizedImage; set => resizedImage = value; }
        public int OriginalWidth { get => originalWidth; set => originalWidth = value; }
        public int OriginalHeight { get => originalHeight; set => originalHeight = value; }
    }
}
