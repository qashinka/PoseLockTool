using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Valve.VR;

namespace WpfAppOpenVr
{
    // ListBoxに表示するためのトラッカー情報を格納するクラス
    public class TrackerInfo : INotifyPropertyChanged
    {
        public string SerialNumber { get; set; }
        private bool isEnabled;
        public bool IsEnabled
        {
            get { return isEnabled; }
            set
            {
                isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class MainWindow : Window
    {
        // UIにバインドするためのトラッカーのリスト
        private ObservableCollection<TrackerInfo> Trackers { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Trackers = new ObservableCollection<TrackerInfo>();
            TrackerListBox.ItemsSource = Trackers;

            // OpenVRの初期化
            var initError = EVRInitError.None;
            OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Utility);

            if (initError != EVRInitError.None)
            {
                StatusTextBlock.Text = $"OpenVRの初期化に失敗: {initError}";
                RefreshButton.IsEnabled = false;
                SaveButton.IsEnabled = false;
            }
            else
            {
                StatusTextBlock.Text = "OpenVRの準備完了。";
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Trackers.Clear();
            StatusTextBlock.Text = "トラッカーを検索中...";

            var system = OpenVR.System;
            if (system == null)
            {
                StatusTextBlock.Text = "OpenVR Systemが利用できません。";
                return;
            }

            // 現在の設定を読み込む
            var settings = OpenVR.Settings;
            var enabledTrackersStr = new StringBuilder(1024);
            EVRSettingsError settingsError = EVRSettingsError.None;
            settings.GetString("PoseLockDriver", "enabled_trackers", enabledTrackersStr, (uint)enabledTrackersStr.Capacity, ref settingsError);
            string currentSettings = enabledTrackersStr.ToString();

            ETrackedPropertyError propertyError = ETrackedPropertyError.TrackedProp_Success;
            for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                if (system.IsTrackedDeviceConnected(i) && system.GetTrackedDeviceClass(i) == ETrackedDeviceClass.GenericTracker)
                {
                    var serialNumberBuilder = new StringBuilder(256);
                    system.GetStringTrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String, serialNumberBuilder, (uint)serialNumberBuilder.Capacity, ref propertyError);

                    if (propertyError == ETrackedPropertyError.TrackedProp_Success)
                    {
                        var serial = serialNumberBuilder.ToString();
                        Trackers.Add(new TrackerInfo
                        {
                            SerialNumber = serial,
                            // 現在の設定に自分のシリアルが含まれていればチェックを入れる
                            IsEnabled = !string.IsNullOrEmpty(currentSettings) && currentSettings.Contains(serial)
                        });
                    }
                }
            }

            StatusTextBlock.Text = $"{Trackers.Count}個のトラッカーが見つかりました。";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // チェックボックスがONになっているトラッカーのシリアル番号をリストアップ
            var enabledSerials = Trackers.Where(t => t.IsEnabled).Select(t => t.SerialNumber).ToList();

            // カンマ区切りの文字列に結合
            string settingsString = string.Join(",", enabledSerials);

            var settings = OpenVR.Settings;
            if (settings != null)
            {
                EVRSettingsError settingsError = EVRSettingsError.None;
                settings.SetString("PoseLockDriver", "enabled_trackers", settingsString, ref settingsError);

                if (settingsError == EVRSettingsError.None)
                {
                    StatusTextBlock.Text = "設定を保存しました。";
                }
                else
                {
                    StatusTextBlock.Text = $"設定の保存に失敗: {settingsError}";
                }
            }
        }

        private void RegisterDriverButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. vrpathreg.exe の場所 (環境に合わせて要変更)
            string steamVrPath = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";
            string vrPathRegPath = Path.Combine(steamVrPath, @"bin\win64\vrpathreg.exe");

            if (!File.Exists(vrPathRegPath))
            {
                StatusTextBlock.Text = "vrpathreg.exe が見つかりません。SteamVRのインストールパスを確認してください。";
                return;
            }

            // 2. ドライバーのマニフェストへのパスを組み立てる
            // このUIアプリの実行ファイルがある場所を基準にする
            string uiAppPath = AppDomain.CurrentDomain.BaseDirectory;
            // そこから一つ上の階層に上がり、driver\simpletrackers を指定
            string driverManifestPath = Path.GetFullPath(Path.Combine(uiAppPath, @"..\..\..\driver\simpletrackers"));

            if (!Directory.Exists(driverManifestPath))
            {
                StatusTextBlock.Text = "ドライバーのフォルダが見つかりません。";
                return;
            }

            // 3. 外部プロセスとして vrpathreg.exe を実行
            try
            {
                ProcessStartInfo procInfo = new ProcessStartInfo();
                procInfo.FileName = vrPathRegPath;
                procInfo.Arguments = $"registerdriver \"{driverManifestPath}\"";
                procInfo.UseShellExecute = false;
                procInfo.RedirectStandardOutput = true;
                procInfo.CreateNoWindow = true;

                Process process = Process.Start(procInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                StatusTextBlock.Text = "ドライバーの登録を試みました。SteamVRを再起動して確認してください。";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"エラー: {ex.Message}";
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // アプリケーション終了時にOpenVRをシャットダウン
            OpenVR.Shutdown();
        }
    }
}
