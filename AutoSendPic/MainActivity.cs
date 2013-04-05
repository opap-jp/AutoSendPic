﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Threading;


using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.OS;
using Android.Hardware;
using Android.OS.Storage;
using AutoSendPic.Model;

namespace AutoSendPic
{
    [Activity(Label = "AutoSendPic", MainLauncher = true, Icon = "@drawable/icon")]
    public class MainActivity : Activity, ISurfaceHolderCallback, Camera.IPictureCallback, Camera.IPreviewCallback
    {
        /// <summary>
        /// カメラのプレビューを表示する所
        /// </summary>
        SurfaceView cameraSurfaceView;

        /// <summary>
        /// 送信中かどうかを格納するフラグ
        /// </summary>
        private volatile bool enableSend = false;

        /// <summary>
        /// 送信中かどうかを格納するフラグ
        /// </summary>
        private volatile bool enableFlash = false;

        /// <summary>
        /// 現在のフレームを撮影するかどうかの制御フラグ
        /// </summary>
        private volatile bool flgReqTakePic = false;

        /// <summary>
        /// カメラへの参照
        /// </summary>
        private Camera camera;

        /// <summary>
        /// 送信用オブジェクト
        /// </summary>
        private DataManager dataManager;

        /// <summary>
        /// カメラ操作同期用オブジェクト
        /// </summary>
        private object cameraSyncObj = new object();
        /// <summary>
        /// カメラ操作同期用オブジェクト
        /// </summary>
        private object timerSyncObj = new object();

        /// <summary>
        /// 汎用同期用オブジェクト
        /// </summary>
        private object syncObj = new object();
        /// <summary>
        /// タイマー
        /// </summary>
        private Timer mainTimer;

        /// <summary>
        /// 設定
        /// </summary>
        private Settings settings;

        /// <summary>
        /// カメラ表示の初期の幅
        /// </summary>
        private int originalWidth;

        /// <summary>
        /// カメラ表示の初期の高さ
        /// </summary>
        private int originalHeight;

        #region Activityイベント
        /// <summary>
        /// 起動時
        /// </summary>
        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            // 画面の向きを固定する
            RequestedOrientation = Android.Content.PM.ScreenOrientation.Landscape;

            // 各種フラグ設定
            this.Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            //設定読み込み
            settings = Settings.Load(this);

            // カメラ表示設定
            cameraSurfaceView = FindViewById<SurfaceView>(Resource.Id.surfaceView1);
            cameraSurfaceView.Holder.AddCallback(this);

            // 各種イベントを登録する
            SetupEvents();

            //カメラを開く
            lock (cameraSyncObj)
            {
                if (camera == null)
                {
                    camera = Camera.Open();
                }
            }
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            base.OnCreateOptionsMenu(menu);

            // メニューの作成
            IMenuItem menuItem1 = menu.Add(0, 0, 0, "設定");

            //アイコンの追加
            menuItem1.SetIcon(Android.Resource.Drawable.IcMenuPreferences);

            //アクションバーに表示            
            menuItem1.SetShowAsAction(ShowAsAction.Always);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case 0:
                    StopSend();
                    Intent intent = new Intent(this, typeof(SettingsActivity));
                    StartActivity(intent);
                    break;
                default:
                    break;
            }

            return true;

        }

        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);

            Android.Util.Log.Debug("AutoSendPic", "Configuration Changed");
        }
        protected override void OnPause()
        {
            base.OnPause();
            StopSend();

            lock (cameraSyncObj)
            {
                if (camera != null)
                {
                    camera.StopPreview();
                    camera.Release();
                    camera = null;
                }
            }
        }
        protected override void OnResume()
        {
            base.OnResume();
            lock (cameraSyncObj)
            {
                if (camera == null)
                {
                    camera = Camera.Open();
                }
            }
        }
        protected override void OnDestroy()
        {
            base.OnDestroy();

            StopTimer();
            StopDataManager();
        }
        #endregion

        /// <summary>
        /// 各種イベントの登録
        /// </summary>
        void SetupEvents()
        {



            //ボタン設定
            Button btnSendStart = FindViewById<Button>(Resource.Id.btnSendStart);
            btnSendStart.Click += delegate
            {
                ToggleSendStatus();
            };


            Button btnExit = FindViewById<Button>(Resource.Id.btnExit);
            btnExit.Click += delegate
            {
                this.Finish();
                System.Environment.Exit(0);
            };

            Button btnFocus = FindViewById<Button>(Resource.Id.btnFocus);
            btnFocus.Click += delegate
            {
                try
                {
                    lock (cameraSyncObj)
                    {
                        camera.AutoFocus(null);
                    }
                }
                catch (Exception ex)
                {
                    showError(ex);
                }
            };

            Button btnToggleFlash = FindViewById<Button>(Resource.Id.btnToggleFlash);
            btnToggleFlash.Click += delegate
            {
                lock (syncObj)
                {
                    enableFlash = !enableFlash;
                }
                ApplyCammeraSettings();
            };
        }

        /// <summary>
        /// 撮影タイマーを開始する
        /// </summary>
        void StartTimer()
        {
            StopTimer();
            lock (timerSyncObj)
            {
                mainTimer = new Timer(new TimerCallback(mainTimerCallback), null, 0, settings.MinInterval * 1000);
            }
        }

        /// <summary>
        /// 撮影タイマーを停止する
        /// </summary>
        void StopTimer()
        {
            lock (timerSyncObj)
            {
                if (mainTimer != null)
                {
                    mainTimer.Dispose();
                    mainTimer = null;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        void StartDataManager()
        {
            StopDataManager();
            lock (syncObj)
            {
                // 送信モジュールを初期化
                dataManager = new DataManager();
                dataManager.PicStorages.Add(new LocalFilePicStorage() { OutputDir = settings.OutputDir, FileNameFormat = "pic_{0:yyyy-MM-dd-HH-mm-ss}.jpg" });
                dataManager.Error += dataManager_Error;

                dataManager.Start();
            }
        }

        void StopDataManager()
        {
            lock (syncObj)
            {
                if (dataManager != null)
                {
                    dataManager.Stop();
                    dataManager.Dispose();
                    dataManager = null;
                }
            }
        }

        void mainTimerCallback(object o)
        {
            try
            {

                lock (syncObj)
                {
                    //送信が有効かどうかチェック
                    if (enableSend && camera != null)
                    {
                        flgReqTakePic = true; //次のプレビューフレームを保存する

                    }
                }


            }
            catch (Exception e)
            {
                showError(e);
            }
            finally
            {
            }
        }

        void dataManager_Error(object sender, ExceptionEventArgs e)
        {
            showError(e.Exception);
        }

        void showError(Exception ex)
        {

            try
            {
                StringBuilder sb = new StringBuilder();
                if (ex == null)
                {
                    sb.Append("NULL Error");
                }
                else
                {
                    sb.AppendFormat(
                        "{0}: {1}",
                        ex.GetType().Name,
                        ex.Message
                    );
                }

                Toast toast = Toast.MakeText(BaseContext, sb.ToString(), Android.Widget.ToastLength.Short);
                toast.Show();
            }
            catch
            {
            }
        }


        public void ToggleSendStatus()
        {

            if (enableSend)
            {
                StopSend();
            }
            else
            {
                StartSend();
            }
        }

        public void StartSend()
        {
            StopSend();

            //設定を再ロード
            settings = Settings.Load(this);
            ApplyCammeraSettings();

            //スタート
            StartTimer();
            StartDataManager();
            enableSend = true;
            RefreshUIStatus();
        }

        public void StopSend()
        {
            //ストップ
            StopDataManager();
            StopTimer();
            enableSend = false;
            RefreshUIStatus();
        }

        public void RefreshUIStatus()
        {

            //画面設定
            Button btnSendStart = FindViewById<Button>(Resource.Id.btnSendStart);
            btnSendStart.Text = enableSend ? "送信停止" : "送信開始";
        }

        public void ApplyCammeraSettings()
        {
            Camera.Size sz, sz2;

            if (originalHeight * originalWidth == 0)
            {
                return; //読み込まれていなかった場合は設定しない
            }


            Camera.Parameters parameters = camera.GetParameters();
            sz = GetOptimalSize(parameters.SupportedPreviewSizes, settings.Width, settings.Height); //最適なサイズを取得
            parameters.SetPreviewSize(sz.Width, sz.Height);
            sz2 = GetOptimalSize(parameters.SupportedPictureSizes, settings.Width, settings.Height);
            parameters.SetPictureSize(sz2.Width, sz2.Height);
            parameters.JpegQuality = 70;


            var modes = parameters.SupportedFlashModes;

            if (enableFlash)
            {
                if (modes.Contains(Camera.Parameters.FlashModeTorch))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeTorch;
                }
                else if (modes.Contains(Camera.Parameters.FlashModeOn))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeOn;
                }
            }
            else
            {
                if (modes.Contains(Camera.Parameters.FlashModeOff))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeOff;
                }
            }

            lock (cameraSyncObj)
            {
                camera.SetParameters(parameters);
            }

            //レイアウト設定の編集

            ViewGroup.LayoutParams lp = cameraSurfaceView.LayoutParameters;
            int ch = sz.Height;
            int cw = sz.Width;
            if ((double)ch / cw > (double)originalWidth / originalHeight)
            {
                lp.Width = originalWidth;
                lp.Height = originalWidth * ch / cw;
            }
            else
            {
                lp.Width = originalHeight * cw / ch;
                lp.Height = originalHeight;
            }
            cameraSurfaceView.LayoutParameters = lp;

        }

        public void ApplyFlashStatus()
        {
            Camera.Parameters parameters = camera.GetParameters();

            var modes = parameters.SupportedFlashModes;

            if (enableFlash)
            {
                if (modes.Contains(Camera.Parameters.FlashModeTorch))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeTorch;
                }
                else if (modes.Contains(Camera.Parameters.FlashModeOn))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeOn;
                }
            }
            else
            {
                if (modes.Contains(Camera.Parameters.FlashModeOff))
                {
                    parameters.FlashMode = Camera.Parameters.FlashModeOff;
                }
            }

            lock (cameraSyncObj)
            {
                camera.SetParameters(parameters);
            }
        }


        #region ISurfaceHolderCallback メンバー

        public void SurfaceChanged(ISurfaceHolder holder, Android.Graphics.Format format, int width, int height)
        {

            try
            {
                // 縦横が入れ替わっている場合の対処
                if (width < height)
                {
                    var t = width;
                    width = height;
                    height = t;
                }

                originalWidth = width;
                originalHeight = height;

                // カメラ設定の編集
                ApplyCammeraSettings();


                // 画面プレビュー開始
                camera.SetPreviewDisplay(cameraSurfaceView.Holder);
                camera.SetPreviewCallback(this);
                camera.StartPreview();

            }
            catch (System.IO.IOException)
            {
            }
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            lock (cameraSyncObj)
            {
                if (camera == null)
                {
                    camera = Camera.Open();
                }
            }
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {

        }

        private Camera.Size GetOptimalSize(IList<Camera.Size> sizes, int w, int h)
        {
            double targetRatio = (double)w / h;
            if (sizes == null)
                return null;

            Camera.Size optimalSize = null;

            int targetHeight = h;


            var sorted_sizes =
                sizes.OrderBy((x) => Math.Abs((double)x.Width / x.Height - targetRatio))
                     .ThenBy((x) => Math.Abs(x.Height - targetHeight));

            optimalSize = sorted_sizes.FirstOrDefault(); //一番差が小さいやつ
            return optimalSize;
        }
        #endregion


        #region IPreviewCallback メンバー

        public void OnPreviewFrame(byte[] data, Camera camera)
        {


            try
            {
                //送信が有効かどうかチェック
                if (!enableSend)
                {
                    return;
                }

                lock (syncObj)
                {
                    if (flgReqTakePic)
                    {
                        flgReqTakePic = false;
                    }
                    else
                    {
                        return;
                    }

                }


                //データを読み取り
                Camera.Parameters parameters = camera.GetParameters();
                Camera.Size size = parameters.PreviewSize;
                using (Android.Graphics.YuvImage image = new Android.Graphics.YuvImage(data, parameters.PreviewFormat,
                        size.Width, size.Height, null))
                {


                    //データをJPGに変換してメモリに保持                    
                    using (MemoryStream ms = new MemoryStream())
                    {
                        image.CompressToJpeg(
                            new Android.Graphics.Rect(0, 0, image.Width, image.Height), 90,
                            ms);

                        ms.Close();
                        PicData pd = new PicData(ms.ToArray(), DateTime.Now); // Closeしてからでないと、ToArrayは正常に取得できない
                        dataManager.PicDataQueue.Enqueue(pd);
                    }


                }


            }
            catch (Exception e)
            {
                showError(e);
            }

        }
        #endregion


        #region IPictureCallback メンバー

        public void OnPictureTaken(byte[] data, Camera camera)
        {


            try
            {

                PicData pd = new PicData(data, DateTime.Now);
                dataManager.PicDataQueue.Enqueue(pd);


            }
            catch (Exception e)
            {
                showError(e);
            }

            lock (cameraSyncObj)
            {
                camera.StartPreview();
            }
            ApplyFlashStatus();
        }

        #endregion
    }
}

