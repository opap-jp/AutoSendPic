using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace AutoSendPic.Model
{
    public abstract class  PicStorage
    {
        public string FileNameFormat { get; set; }

        public abstract bool Save(Stream dataToSave);

        event EventHandler<Exception> Error;

        public string MakeFileName()
        {
            string fileName = string.Format(FileNameFormat, DateTime.Now);
            return fileName;
        }

    }
}