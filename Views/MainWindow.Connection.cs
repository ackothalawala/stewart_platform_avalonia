using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Globalization;
using stewart_platform.Services;

namespace stewart_platform
{
    public partial class MainWindow
    {
        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected || sending || commandsInhibited) return;
            if (isClosedLoop && !FeedbackFresh())
            { InhibitCommands("Sensor timeout"); return; }
            sending = true;
            try
            {
                string payload = ServoCommandFormatter.Format(platform);
                await connection.SendAsync(payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Servo send failed: " + ex.Message);
                InhibitCommands("Servo communication failure");
            }
            finally { sending = false; }
        }


        private void ProcessIncomingData(string data)
        {
            if (!SensorFeedbackParser.TryParse(data, out SensorFeedback reading)) return;
            Dispatcher.UIThread.Post(() =>
            {
                current_roll = reading.Roll;
                current_pitch = reading.Pitch;
                current_yaw = reading.Yaw;
                hasFeedback = true;
                lastFeedbackUtc = DateTime.UtcNow;
            });
        }


        public void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            var btn = this.FindControl<Button>("BtnConnect");
            if (btn?.Content?.ToString() == "Disconnect")
                Disconnect();
            else
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = true;
                RefreshPorts();
            }
        }


        private void Disconnect()
        {
            isConnected = false;
            sendTimer.Stop();
            isClosedLoop = false;
            levelingPhase = LevelingPhase.OpenLoop;
            heldRollTrim = heldPitchTrim = 0;
            levelController.Reset();
            UpdateModeUI();
            var btn = this.FindControl<Button>("BtnConnect");
            if (btn != null) btn.Content = "Connect Platform";
            connection.Disconnect();
            hasFeedback = false;
            var ts = this.FindControl<TextBlock>("TxtStatus");
            if (ts != null) { ts.Text = "Disconnected"; ts.Foreground = Brushes.Red; }
        }


        public void BtnModalCancel_Click(object? sender, RoutedEventArgs e)
        {
            var modal = this.FindControl<Grid>("ModalOverlay");
            if (modal != null) modal.IsVisible = false;
        }


        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e) => RefreshPorts();


        private void RefreshPorts()
        {
            var ports = PlatformConnectionService.AvailablePorts();
            var cu = this.FindControl<ComboBox>("ComboUsbPort");
            var cd = this.FindControl<ComboBox>("ComboDonglePort");
            if (cu != null) { cu.ItemsSource = ports; if (cu.ItemCount > 0) cu.SelectedIndex = 0; }
            if (cd != null) { cd.ItemsSource = ports; if (cd.ItemCount > 0) cd.SelectedIndex = 0; }
        }


        public async void BtnModalConnect_Click(object? sender, RoutedEventArgs e)
        {
            bool wifi = this.FindControl<RadioButton>("RadioWifi")?.IsChecked == true;
            bool usb = this.FindControl<RadioButton>("RadioUsb")?.IsChecked == true;
            isWifiMode = wifi;
            var ts = this.FindControl<TextBlock>("TxtStatus");
            try
            {
                if (wifi)
                {
                    string url = this.FindControl<TextBox>("TxtWifiUrl")?.Text ?? "ws://192.168.4.1:81";
                    await connection.ConnectWebSocketAsync(url);
                }
                else
                {
                    string? port = this.FindControl<ComboBox>(usb ? "ComboUsbPort" : "ComboDonglePort")?.SelectedItem as string;
                    if (string.IsNullOrEmpty(port)) throw new Exception("No COM port selected.");
                    connection.ConnectSerial(port, config.BaudRate);
                }
                isConnected = true;
                commandsInhibited = false;
                hasFeedback = false;
                isClosedLoop = false;
                levelingPhase = LevelingPhase.OpenLoop;
                heldRollTrim = heldPitchTrim = 0;
                levelController.Reset();
                UpdateModeUI();
                sendTimer.Start();
                if (ts != null)
                {
                    ts.Text = wifi ? "Connected (Wi-Fi)" : "Connected (USB Serial)";
                    ts.Foreground = Brushes.Green;
                }
                var btn = this.FindControl<Button>("BtnConnect");
                if (btn != null) btn.Content = "Disconnect";
            }
            catch (Exception ex)
            {
                connection.Disconnect();
                isConnected = false;
                sendTimer.Stop();
                System.Diagnostics.Debug.WriteLine($"[Connect] {ex.Message}");
                if (ts != null) { ts.Text = "Connection Failed!"; ts.Foreground = Brushes.Red; }
            }
            finally
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = false;
            }
        }

    }
}
