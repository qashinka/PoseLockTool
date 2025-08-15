using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks; // Add this for async operations
using System.Windows;
using Microsoft.Win32;
using Valve.VR;

namespace WpfAppOpenVr
{
    public class TrackerInfo : INotifyPropertyChanged
    {
        public uint DeviceId { get; set; }
        public string SerialNumber { get; set; }
        public string ModelNumber { get; set; }
        public string TrackerType { get; set; } // "Physical" or "Virtual"
        public bool IsVirtual => TrackerType == "Virtual";
        public string CurrentRole { get; set; }

        // --- Properties for DataGrid ComboBoxes ---

        public ObservableCollection<TrackerInfo> AvailablePhysicalTrackers { get; set; }
        public ObservableCollection<string> AvailableRoles { get; set; }

        private TrackerInfo _selectedProxyTarget;
        public TrackerInfo SelectedProxyTarget
        {
            get => _selectedProxyTarget;
            set { _selectedProxyTarget = value; OnPropertyChanged(nameof(SelectedProxyTarget)); }
        }

        private string _selectedRole;
        public string SelectedRole
        {
            get => _selectedRole;
            set { _selectedRole = value; OnPropertyChanged(nameof(SelectedRole)); }
        }

        public string DisplayName => $"{ModelNumber} ({SerialNumber})";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class MainWindow : Window
    {
        private const string POSE_LOCK_DRIVER_SETTINGS_SECTION = "PoseLockDriver";
        private const string POSE_LOCK_PROXY_SETTINGS_SECTION = "PoseLockProxy";

        // UIにバインドするためのトラッカーのリスト
        private ObservableCollection<TrackerInfo> Trackers { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Trackers = new ObservableCollection<TrackerInfo>();
            TrackerDataGrid.ItemsSource = Trackers;

            // OpenVRの初期化
            var initError = EVRInitError.None;
            OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Utility);

            if (initError != EVRInitError.None)
            {
                StatusTextBlock.Text = $"OpenVRの初期化に失敗: {initError}";
                RefreshButton.IsEnabled = false;
                SaveButton.IsEnabled = false;
                SaveNumTrackersButton.IsEnabled = false;
            }
            else
            {
                StatusTextBlock.Text = "OpenVRの準備完了。";
                LoadDriverSettings();
                RefreshButton_Click(null, null); // Load trackers on startup
            }
        }

        private void LoadDriverSettings()
        {
            var settings = OpenVR.Settings;
            if (settings == null) return;

            EVRSettingsError settingsError = EVRSettingsError.None;
            int numTrackers = settings.GetInt32(POSE_LOCK_DRIVER_SETTINGS_SECTION, "num_virtual_trackers", ref settingsError);

            if (settingsError == EVRSettingsError.None)
            {
                NumTrackersTextBox.Text = numTrackers.ToString();
            }
            else
            {
                NumTrackersTextBox.Text = "0";
            }
        }

        private void SaveNumTrackersButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(NumTrackersTextBox.Text, out int numTrackers) || numTrackers < 0 || numTrackers > 10)
            {
                StatusTextBlock.Text = "エラー: 0から10の間の整数を入力してください。";
                return;
            }

            var settings = OpenVR.Settings;
            if (settings != null)
            {
                EVRSettingsError settingsError = EVRSettingsError.None;
                settings.SetInt32(POSE_LOCK_DRIVER_SETTINGS_SECTION, "num_virtual_trackers", numTrackers, ref settingsError);

                if (settingsError == EVRSettingsError.None)
                {
                    StatusTextBlock.Text = $"仮想トラッカーの数を {numTrackers} に設定しました。SteamVRを再起動すると適用されます。";
                }
                else
                {
                    StatusTextBlock.Text = $"設定の保存に失敗: {settingsError}";
                }
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (OpenVR.System == null)
            {
                StatusTextBlock.Text = "OpenVR Systemが利用できません。";
                return;
            }

            StatusTextBlock.Text = "トラッカーを検索中...";
            Trackers.Clear();

            var physicalTrackers = new List<TrackerInfo>();
            var virtualTrackers = new List<TrackerInfo>();

            var roles = Enum.GetNames(typeof(ETrackedControllerRole)).ToList();

            // 1. Find all trackers and sort them into physical and virtual lists
            for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                if (!OpenVR.System.IsTrackedDeviceConnected(i)) continue;
                if (OpenVR.System.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;

                var serial = GetProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String);
                var model = GetProperty(i, ETrackedDeviceProperty.Prop_ModelNumber_String);
                
                ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
                var roleId = OpenVR.System.GetInt32TrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32, ref err);
                var role = (err == ETrackedPropertyError.TrackedProp_Success) ? ((ETrackedControllerRole)roleId).ToString() : "Invalid";

                var tracker = new TrackerInfo
                {
                    DeviceId = i,
                    SerialNumber = serial,
                    ModelNumber = model,
                    CurrentRole = role,
                    AvailableRoles = new ObservableCollection<string>(roles)
                };

                if (model.Contains("MyTrackerModelNumber")) // This is how we identify our virtual trackers
                {
                    tracker.TrackerType = "Virtual";
                    virtualTrackers.Add(tracker);
                }
                else
                {
                    tracker.TrackerType = "Physical";
                    physicalTrackers.Add(tracker);
                }
            }

            // 2. Populate UI lists and set current proxy selections
            var availablePhysicalTrackersWithNone = new ObservableCollection<TrackerInfo>(physicalTrackers);
            availablePhysicalTrackersWithNone.Insert(0, new TrackerInfo { SerialNumber = "None", ModelNumber = "" }); // Add a "None" option

            foreach (var vt in virtualTrackers)
            {
                vt.AvailablePhysicalTrackers = availablePhysicalTrackersWithNone;
                vt.SelectedRole = vt.CurrentRole;

                // Read current proxy setting for this virtual tracker
                string key = "proxy_target_for_" + vt.SerialNumber;
                EVRSettingsError settingsErr = EVRSettingsError.None;
                int targetIndex = OpenVR.Settings.GetInt32(POSE_LOCK_PROXY_SETTINGS_SECTION, key, ref settingsErr);

                if (settingsErr == EVRSettingsError.None && targetIndex != -1)
                {
                    vt.SelectedProxyTarget = physicalTrackers.FirstOrDefault(pt => pt.DeviceId == targetIndex);
                }
            }

            // 3. Add all trackers to the main list for the DataGrid
            physicalTrackers.ForEach(Trackers.Add);
            virtualTrackers.ForEach(Trackers.Add);

            StatusTextBlock.Text = $"{Trackers.Count}個のトラッカーが見つかりました ({physicalTrackers.Count} physical, {virtualTrackers.Count} virtual)。";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "設定を保存中...";
            var settings = OpenVR.Settings;
            if (settings == null) return;

            foreach (var tracker in Trackers)
            {
                if (!tracker.IsVirtual) continue;

                EVRSettingsError err = EVRSettingsError.None;

                // --- 1. Save the proxy target setting ---
                string proxyKey = "proxy_target_for_" + tracker.SerialNumber;
                if (tracker.SelectedProxyTarget != null && tracker.SelectedProxyTarget.DeviceId != 0) // 0 is not a valid device ID
                {
                    settings.SetInt32(POSE_LOCK_PROXY_SETTINGS_SECTION, proxyKey, (int)tracker.SelectedProxyTarget.DeviceId, ref err);
                }
                else
                {
                    // No proxy selected, remove the key
                    settings.RemoveKeyInSection(POSE_LOCK_PROXY_SETTINGS_SECTION, proxyKey, ref err);
                }

                // --- 2. Set the role for the virtual tracker and disable the physical one ---
                string roleKey = $"/devices/simpletrackers/{tracker.SerialNumber}";
                if (!string.IsNullOrEmpty(tracker.SelectedRole) && tracker.SelectedRole != ETrackedControllerRole.Invalid.ToString() && tracker.SelectedRole != "None")
                {
                    settings.SetString(OpenVR.k_pch_SteamVR_Section, roleKey, tracker.SelectedRole.ToLower(), ref err);

                    // Also disable the role of the physical tracker that is being proxied
                    if (tracker.SelectedProxyTarget != null && tracker.SelectedProxyTarget.DeviceId != 0)
                    {
                        string physicalRoleKey = $"/devices/vive/vive_tracker{tracker.SelectedProxyTarget.SerialNumber}";
                        settings.SetString(OpenVR.k_pch_SteamVR_Section, physicalRoleKey, "disabled", ref err);
                    }
                }
                else
                {
                    // If role is None or Invalid, remove the key
                    settings.RemoveKeyInSection(OpenVR.k_pch_SteamVR_Section, roleKey, ref err);
                }
            }

            StatusTextBlock.Text = "設定を保存しました。変更の完全な適用にはSteamVRの再起動が必要な場合があります。";
        }

        private async void RegisterDriverButton_Click(object sender, RoutedEventArgs e)
        {
            // --- Step 1: Register the driver ---
            string steamVrPath = GetSteamVRPath();
            if (string.IsNullOrEmpty(steamVrPath))
            {
                StatusTextBlock.Text = "SteamVRのインストールフォルダが見つかりません";
                return;
            }

            string vrPathRegPath = Path.Combine(steamVrPath, @"bin\win64\vrpathreg.exe");
            if (!File.Exists(vrPathRegPath))
            {
                StatusTextBlock.Text = "vrpathreg.exe が見つかりません";
                return;
            }

            string driverManifestPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, 
                "driver", 
                "simpletrackers"
            ));
            if (!Directory.Exists(driverManifestPath))
            {
                StatusTextBlock.Text = $"ドライバーのフォルダが見つかりません: {driverManifestPath}";
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = vrPathRegPath,
                Arguments = $"adddriver \"{driverManifestPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true, // Capture standard output
                RedirectStandardError = true   // Capture standard error
            };

            string output = "(empty)";
            string error = "(empty)";

            try
            {
                using (Process process = Process.Start(psi))
                {
                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        StatusTextBlock.Text = $"登録失敗 (Code:{process.ExitCode}): {error} {output}";
                        return; // Stop if registration fails
                    }
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"ドライバーの登録実行に失敗: {ex.Message}";
                return; // Stop if registration fails
            }

            // --- Step 2: Ask the user for confirmation ---
            var result = MessageBox.Show(
                "ドライバーの登録コマンドが正常に完了しました。SteamVRを再起動しますか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result == MessageBoxResult.No)
            {
                StatusTextBlock.Text = "ドライバーは登録されました。手動でSteamVRを再起動してください。";
                return;
            }

            // --- Step 3: Restart SteamVR without closing the UI ---
            try
            {
                StatusTextBlock.Text = "UIのOpenVR接続を一旦切断します...";
                OpenVR.Shutdown();
                await Task.Delay(1000); // Give it a second to release resources

                StatusTextBlock.Text = "SteamVRプロセスを終了しています...";
                foreach (var process in Process.GetProcessesByName("vrserver"))
                {
                    process.Kill();
                }
                await Task.Delay(3000); // Wait for the process to die

                StatusTextBlock.Text = "SteamVRを起動しています... (15秒ほどお待ちください)";
                string vrStartupPath = Path.Combine(steamVrPath, "bin", "win64", "vrstartup.exe");
                if (File.Exists(vrStartupPath))
                {
                    Process.Start(vrStartupPath);
                }
                else
                {
                    StatusTextBlock.Text = "vrstartup.exeが見つかりません。手動で起動してください。";
                    return;
                }

                await Task.Delay(15000); // Give SteamVR plenty of time to start

                StatusTextBlock.Text = "SteamVRに再接続しています...";
                var initError = EVRInitError.None;
                OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Utility);

                if (initError == EVRInitError.None)
                {
                    StatusTextBlock.Text = "再接続成功。トラッカーリストを更新します。";
                    RefreshButton_Click(null, null); // Refresh the tracker list
                }
                else
                {
                    StatusTextBlock.Text = $"SteamVRへの再接続に失敗: {initError}";
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"再起動中にエラーが発生: {ex.Message}";
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            OpenVR.Shutdown();
        }

        // Helper function to get a tracked device property
        private string GetProperty(uint deviceId, ETrackedDeviceProperty prop)
        {
            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            var buffer = new StringBuilder(256);
            OpenVR.System.GetStringTrackedDeviceProperty(deviceId, prop, buffer, (uint)buffer.Capacity, ref error);
            return error == ETrackedPropertyError.TrackedProp_Success ? buffer.ToString() : "";
        }

        // Helper function to find SteamVR path
        private string GetSteamVRPath()
        {
            try
            {
                // Check the 64-bit registry location first
                var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\SteamVR");
                if (key == null)
                {
                    // If not found, check the 32-bit compatibility location
                    key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\SteamVR");
                }

                string path = key?.GetValue("InstallPath") as string;

                // A final check to see if the directory actually exists
                if (!string.IsNullOrEmpty(path) && Directory.Exists(Path.Combine(path, "bin", "win64")))
                {
                    return path;
                }
            }
            catch { /* Ignore registry access errors */ }

            // If registry fails, try a common default path
            string defaultPath = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";
            if (Directory.Exists(Path.Combine(defaultPath, "bin", "win64")))
            {
                return defaultPath;
            }

            return null;
        }
    }
}
