using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CMP90HX.Control
{
    public partial class MainWindow : Window
    {
        sealed class DeviceStatus
        {
            internal string GpuName = "未发现 90HX";
            internal int? ProblemCode;
            internal string DriverState = "未安装";
            internal int Count;
        }

        readonly RuntimePaths runtime;

        readonly string logs;
        readonly DispatcherTimer timer;
        readonly Stopwatch elapsed = new Stopwatch();
        readonly StringBuilder runText = new StringBuilder();
        string runDirectory;
        string runId;

        string activeLabel;
        string displayedStage;

        int ticks;
        bool busy;
        bool refreshing;

        bool closed;
        bool startupInitialized;

        public MainWindow()
        {
            InitializeComponent();
            SizeChanged += delegate { SummaryCard.MaxHeight = Math.Max(190, ActualHeight - 480); };
            runtime = new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);
            logs = App.Session!=null?App.Session.DirectoryPath:runtime.Logs;
            EnvironmentText.Text = "v" + RuntimePaths.Version + " · 核心 469dc0c";
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += Timer_Tick;
            Closed += delegate { closed = true; timer.Stop(); };
        }

        void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            e.Handled = true;
            if (e.Uri == null || !e.Uri.IsAbsoluteUri ||
                (e.Uri.Scheme != Uri.UriSchemeHttps && e.Uri.Scheme != Uri.UriSchemeHttp)) return;
            try {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            } catch (Exception error) {
                MessageBox.Show(this, "无法打开链接：" + error.Message, "打开链接失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.PreviewState != null) {
                ShowPreview(App.PreviewState);
                if (App.SnapshotPath != null)
                    await Dispatcher.InvokeAsync(new Action(SavePreview), DispatcherPriority.ApplicationIdle);
                return;
            }
            if(startupInitialized) return;
            startupInitialized = true;
            AppendLog("欢迎使用 CMP 90HX 控制台。\r\n\r\n首次使用请进行环境检查。\r\n旧日志已清理，本次操作记录会自动保存。\r\n");
            RefreshStatus();
            timer.Start();
            if(App.DisableAutoUnlock) {
                UnlockReadDetail.Text="已禁用启动自动读取；可点击“刷新解锁状态”手动采集。";
                AppendLog("AUTO_UNLOCK_DISABLED: 已跳过启动自动读取；手动读取和手动解锁仍可用。\r\n");
            } else await RunWorkflow("Status", "解锁状态");
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!busy || App.PreviewState != null) return;
            e.Cancel = true;
            OperationDetail.Text = "操作仍在运行，请等待结果。硬件流程无法通过关闭界面安全取消。";
        }

        async void RefreshStatus()
        {
            if (refreshing || closed || App.PreviewState != null) return;
            refreshing = true;
            RefreshButton.IsEnabled = false;
            try {
                DeviceStatus status = await Task.Run((Func<DeviceStatus>)ReadDeviceStatus);
                if (closed) return;
                GpuText.Text = status.Count > 1 ? "检测到 " + status.Count + " 张 90HX" : status.GpuName;
                PnpText.Text = status.ProblemCode.HasValue
                    ? (status.ProblemCode.Value == 0 ? "正常 · Code 0" :
                       status.ProblemCode.Value == 22 ? "已停用 · Code 22" : "异常 · Code " + status.ProblemCode.Value)
                    : "未检测到当前设备";
                DriverText.Text = status.DriverState;
                UpdatedText.Text = "状态更新于 " + DateTime.Now.ToString("HH:mm:ss");
                if (status.Count == 0) SetBadge("未找到 CMP 90HX", "#FFF5E6", "#995E0C");
                else if (status.Count != 1) SetBadge("需要唯一一张 90HX", "#FFF1F0", "#A83B32");
                else if (status.ProblemCode == 22) SetBadge("显卡已停用", "#FFF5E6", "#995E0C");
                else if (status.ProblemCode != 0) SetBadge("需要检查设备", "#FFF1F0", "#A83B32");
                else if (status.DriverState == "未安装") SetBadge("请先安装 DMA 驱动", "#FFF5E6", "#995E0C");
                else if (status.DriverState != "运行中") SetBadge("DMA 驱动待启动", "#FFF5E6", "#995E0C");
                else SetBadge("设备就绪", "#E8F6EE", "#1D7546");
            } catch (Exception error) {
                if (closed) return;
                SetBadge("状态读取失败", "#FFF1F0", "#A83B32");
                UpdatedText.Text = "点击刷新重试";
                UpdatedText.ToolTip = error.Message;
            } finally {
                refreshing = false;
                RefreshButton.IsEnabled = true;
            }
        }

        static DeviceStatus ReadDeviceStatus()
        {
            DeviceStatus state = new DeviceStatus();
            var devices = NativePnp.Enumerate();
            state.Count = devices.Count;
            if (devices.Count > 0) { state.GpuName = devices[0].Name; state.ProblemCode = (int)devices[0].Problem; }
            state.DriverState = DriverService.CurrentState();
            return state;
        }
        void SetBadge(string text, string background, string foreground)
        {
            OverallText.Text = text;
            OverallBadge.Background = (Brush)new BrushConverter().ConvertFromString(background);
            OverallText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground);
        }

        void Timer_Tick(object sender, EventArgs e)
        {
            if (busy) {

                ElapsedText.Text = "已用时 " + FormatElapsed(elapsed.Elapsed);
            }
            if (++ticks % 10 == 0) RefreshStatus();
        }

        static string FormatElapsed(TimeSpan duration)
        {
            return ((int)duration.TotalMinutes).ToString("00") + ":" + duration.Seconds.ToString("00");
        }

        static string Tail(string text, int maxChars)
        {
            return text.Length <= maxChars ? text : text.Substring(text.Length - maxChars);
        }

        void AcceptOutput(string text)
        {
            runText.Append(text);
            AppendLog(text);
            if (!busy) return;
            MatchCollection stages = Regex.Matches(runText.ToString(), @"(?<![A-Z_])STAGE=([A-Z0-9_x]+)");
            if (stages.Count > 0) {
                string stage = stages[stages.Count - 1].Groups[1].Value;
                if (stage != displayedStage) {
                    displayedStage = stage;
                    OperationTitle.Text = "正在" + activeLabel + " · " + StageLabel(stage);
                    OperationDetail.Text = "阶段：" + stage + "。请等待操作完成，耗时较长请耐心等待。";
                }
            }
            if (text.Contains("Re-enabling the original")) {
                OperationTitle.Text = "正在恢复并验证设备";
                OperationDetail.Text = "等待 NVIDIA 驱动接管，再采样验证 Gen2、链路宽度和解锁状态。";
            }
        }

        static string StageLabel(string stage)
        {
            switch (stage) {
                case "BRIDGE_ECAM_PREFLIGHT": return "检查上游桥";
                case "INITIAL_DUAL_SBR": return "复位设备";
                case "COMPUTE": return "计算功能";
                case "GRAPHICS": return "图形功能";
                case "GEN2_RETRAIN_VERIFY": return "验证 Gen2";
                case "FULL_UNLOCK_VERIFIED_WHILE_DISABLED": return "停用状态验证";
                default: return stage.StartsWith("GEN2_") ? "配置 Gen2 链路" : stage;
            }
        }

        void AppendLog(string text)
        {
            LogBox.AppendText(text);
            if (LogBox.Text.Length > 90000) LogBox.Text = Tail(LogBox.Text, 70000);
            if (FollowLogs.IsChecked == true) LogBox.ScrollToEnd();
        }

        void SetBusy(bool value)
        {
            busy = value;
            bool enabled = !value && App.PreviewState == null;
            CheckButton.IsEnabled = enabled;
            TaskManagerButton.IsEnabled = enabled;
            PowerManagerButton.IsEnabled = enabled;
            UnlockButton.IsEnabled = enabled;
            ConservativeTiming.IsEnabled = enabled;
            ReadUnlockButton.IsEnabled = enabled;
            PrepareDriverButton.IsEnabled = enabled;
            UninstallButton.IsEnabled = enabled;
            ActivityBar.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        }

        async void UninstallButton_Click(object sender, RoutedEventArgs e)
        {
            if(busy || App.PreviewState!=null) return;
            if(MessageBox.Show(this,"将停止省电并恢复自动性能，卸载驱动、两个签名根证书、自动解锁和省电计划任务。下次重启后清空 C:\\ProgramData\\CMP90HX 中的日志、配置和后台副本。完成后程序将退出。是否继续？",
                "一键卸载",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
            SetBusy(true);
            OperationTitle.Text="正在卸载";
            OperationDetail.Text="正在恢复性能并移除驱动、证书和计划任务。";
            try {
                await Task.Run(()=>TaskManagement.UninstallAll(line=>Dispatcher.BeginInvoke(new Action(()=>AppendLog(line+"\r\n")))));
                MessageBox.Show(this,"驱动、证书和计划任务已卸载。请重启 Windows；重启后将自动清空程序数据目录。","卸载完成",MessageBoxButton.OK,MessageBoxImage.Information);
                SetBusy(false);
                Application.Current.Shutdown(0);
            } catch(Exception error) {
                OperationTitle.Text="卸载未完成";
                OperationDetail.Text=error.Message;
                AppendLog("UNINSTALL_FAILED: "+error+"\r\n");
                SetBusy(false);
            }
        }

        async Task RunWorkflow(string mode, string label)
        {
            if (busy || App.PreviewState != null) return;
            SetBusy(true);
            activeLabel = label;
            displayedStage = null;
            OperationNotice.Visibility = Visibility.Collapsed;
            if (mode == "Status" || mode == "Unlock") {
                LinkText.Text = ComputeText.Text = GraphicsText.Text = "待读取";
                UnlockReadDetail.Text = "等待本次采集；旧快照不代表当前状态。";
            }
            elapsed.Restart();
            runId = Guid.NewGuid().ToString("N");
            runDirectory = Path.Combine(logs, runId);
            OperationTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#182536");
            OperationTitle.Text = "正在" + label;
            OperationDetail.Text = mode == "Environment" ? "正在检查文件、核心、证书、驱动和硬件。" :
                "已具备管理员权限；正在执行本机工作流。";
            ElapsedText.Text = "已用时 00:00";
            FooterText.Text = label + " · 运行中";
            SessionText.Text = "本次记录 · " + DateTime.Now.ToString("HH:mm:ss");
            SessionText.ToolTip = runDirectory;
            LogBox.Clear();
            runText.Clear();
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] 开始" + label + "\r\n日志目录：" + runDirectory + "\r\n\r\n");
            try {
                int result = await RunNative(mode);

                PresentResult(mode, label, result);
                if (mode == "Status" || mode == "Environment" || mode == "Unlock" || mode == "Verify") {
                    string snapshot = Path.Combine(runDirectory, "unlock-status.json");
                    if (mode=="Status" && (runText.ToString().Contains("UNLOCK_STATUS_DRIVER_NOT_INSTALLED") || runText.ToString().Contains("DMA_DRIVER_NOT_INSTALLED"))) {
                        LinkText.Text = ComputeText.Text = GraphicsText.Text = "未读取";
                        UnlockReadDetail.Text = "尚未安装 CMP90HXDma 驱动，无法读取解锁状态。请先在“驱动管理”中安装驱动。";
                        UnlockReadDetail.ToolTip = null;
                    }
                    else if (result == 0 && File.Exists(snapshot)) DisplayUnlockSnapshot(snapshot);
                    else {
                        LinkText.Text = ComputeText.Text = GraphicsText.Text = "读取失败";
                        UnlockReadDetail.Text = "本次未取得有效快照，请查看日志。";
                    }
                }
                AppendLog("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + label + "结束，退出码 " + result + "\r\n");
            } catch (Win32Exception error) {
                OperationTitle.Text = error.NativeErrorCode == 1223 ? "已取消管理员授权" : label + "无法启动";
                OperationDetail.Text = error.Message;
                FooterText.Text = "未执行";
                AppendLog("\r\n" + OperationTitle.Text + "：" + error.Message + "\r\n");
            } catch (Exception error) {
                OperationTitle.Text = label + "无法完成";
                OperationDetail.Text = error.Message;
                FooterText.Text = "失败";
                AppendLog("\r\n错误：" + error + "\r\n");
            } finally {
                elapsed.Stop();
                ElapsedText.Text = "总用时 " + FormatElapsed(elapsed.Elapsed);
                SetBusy(false);
                RefreshStatus();
            }
        }

        void PresentResult(string mode, string label, int result)
        {
            OperationNotice.Visibility = Visibility.Collapsed;
            string output = runText.ToString();
            if(mode=="Status" && (output.Contains("UNLOCK_STATUS_DRIVER_NOT_INSTALLED") || output.Contains("DMA_DRIVER_NOT_INSTALLED"))) {
                OperationTitle.Text = "解锁状态未读取";
                OperationTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#667085");
                OperationDetail.Text = "尚未安装 CMP90HXDma 驱动，请先在“驱动管理”中安装驱动。";
                FooterText.Text = "解锁状态 · 未安装驱动";
                return;
            }
            bool disabledOnly = output.Contains("FULL_UNLOCK_VERIFIED_WHILE_DISABLED_ONLY");
            bool verified = output.Contains("FULL_UNLOCK_VERIFIED_AFTER_NVIDIA_REATTACH");
            bool confirmed = mode == "Install" ? output.Contains("DMA_DRIVER_READY") : mode == "Status" ? output.Contains("UNLOCK_STATUS_CAPTURED") :
                mode == "Environment" ? output.Contains("ENVIRONMENT_CHECK_PASSED") :
                mode == "Unlock" ? disabledOnly || verified : verified;
            if (result == 0 && confirmed) {
                OperationTitle.Text = disabledOnly ? "解锁通过 · 显卡保持停用" : label + "已完成";
                OperationTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#1D7546");
                OperationDetail.Text = disabledOnly
                    ? "停用状态下验证通过。启用显卡后，点击“刷新解锁状态”检查 NVIDIA 接管结果。"
                    : verified ? "采样验证通过：PnP Code 0、两端 Gen2、链路宽度与解锁值保持。"
                    : mode == "Install" ? "已签名 DMA 驱动已安装并启动，协议检查通过。" : mode == "Status" ?
                      (output.Contains("UNLOCK_STATUS_VERIFIED") ? "状态已更新：PnP Code 0、两端 Gen2、计算与图形解锁值符合预期。" : "状态已更新，部分条件未达到完整解锁要求；请查看各项状态。")
                    : "文件、核心、两个系统根证书、DMA 驱动及硬件预检通过。";
                FooterText.Text = label + " · 成功";
                return;
            }
            OperationTitle.Text = result == 0 ? label + "结果待确认" : label + "未通过";
            OperationTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#A83B32");
            if (output.Contains("DMA_RETAINED") || output.Contains("QUARANTINED"))
                OperationDetail.Text = "DMA 所有权未释放。保持显卡停用，完全关机后重新开机，再启动驱动；不要直接重试。";
            else if (output.Contains("DMA_DRIVER_UPDATE_REQUIRES_REBOOT"))
                OperationDetail.Text = "旧 DMA 驱动仍在使用中。重启后打开“驱动管理”安装当前驱动。";
            else if (output.Contains("DMA_DRIVER_NOT_INSTALLED"))
                OperationDetail.Text = "尚未安装 CMP90HXDma。打开“驱动管理”安装证书和驱动。";
            else if (output.Contains("CMP90HXDma start failed"))
                OperationDetail.Text = "DMA 驱动启动失败。请查看日志中的系统错误码，并检查驱动签名及本次启动的签名策略。";
            else {
                MatchCollection failures = Regex.Matches(output, @"(?m)(?:FAILED_STAGE[^\r\n]*|FAILED: [^\r\n]*|GUI_WORKFLOW_FAILED[^\r\n]*)");
                OperationDetail.Text = failures.Count > 0 ? Tail(failures[failures.Count - 1].Value, 240)
                    : "退出码 " + result + "，未取得完整成功记录。请打开本次日志目录查看详细结果。";
            }
            FooterText.Text = label + " · " + (result == 0 ? "待确认" : "失败");
        }

        async Task<int> RunNative(string mode)
        {
            Directory.CreateDirectory(runDirectory);
            bool conservative = ConservativeTiming.IsChecked == true;
            int result = await Task.Run(() => {
                using (var writer = new StreamWriter(Path.Combine(runDirectory,"workflow.log"),false,Encoding.UTF8)) {
                    writer.AutoFlush = true;
                    Action<string> log = line => {
                        string value = line + "\r\n";
                        lock(writer) writer.Write(value);
                        if(!closed) Dispatcher.BeginInvoke(new Action(() => AcceptOutput(value)));
                    };
                    var platform = new WindowsWorkflowPlatform(runtime,log);
                    return new NativeWorkflow(runtime,platform,log,conservative:conservative).Execute(mode,runDirectory);
                }
            });
            await Dispatcher.InvokeAsync(delegate { },DispatcherPriority.Background);
            return result;
        }
        void RefreshButton_Click(object sender, RoutedEventArgs e) { RefreshStatus(); }
        async void ReadUnlockButton_Click(object sender, RoutedEventArgs e) { await RunWorkflow("Status", "解锁状态"); }

        void DisplayUnlockSnapshot(string path)
        {
            UnlockSnapshot snapshot = UnlockSnapshot.Parse(File.ReadAllText(path));
            LinkText.Text = UnlockSnapshot.DescribeLink(snapshot.Gpu);
            ComputeText.Text = snapshot.FunctionState(0x82381c, 0x88888888, 0x823820, 8);
            GraphicsText.Text = snapshot.FunctionState(0x823830, 4);
            DateTime timestamp;
            string time = DateTime.TryParse(snapshot.TimestampUtc, out timestamp) ? timestamp.ToLocalTime().ToString("HH:mm:ss") : "时间未知";
            UnlockReadDetail.Text = "采集于 " + time + " · 上游桥 " + UnlockSnapshot.DescribeLink(snapshot.Bridge) + "。功能依据寄存器值；未进行负载测试。";
            UnlockReadDetail.ToolTip = path;
        }
        void OpenManagement(bool tasks)
        {
            if(busy || App.PreviewState!=null) return;
            SetBusy(true);
            try {new ManagementWindow(this,tasks,runtime,logs) {Owner=this}.ShowDialog();}
            finally {SetBusy(false);RefreshStatus();}
        }
        void PrepareDriverButton_Click(object sender, RoutedEventArgs e) { OpenManagement(false); }
        void TaskManagerButton_Click(object sender, RoutedEventArgs e) { OpenManagement(true); }
        void PowerManagerButton_Click(object sender, RoutedEventArgs e)
        {
            if(busy || App.PreviewState!=null) return;
            SetBusy(true);
            try {new PowerWindow(this,runtime,logs) {Owner=this}.ShowDialog();}
            finally {SetBusy(false);RefreshStatus();}
        }
        async void CheckButton_Click(object sender, RoutedEventArgs e) { await RunWorkflow("Environment", "环境检查"); }
        async void UnlockButton_Click(object sender, RoutedEventArgs e) { await RunWorkflow("Unlock", "解锁"); }

        void CopyLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(LogBox.Text); FooterText.Text = "日志已复制"; }
            catch (Exception error) { FooterText.Text = "复制失败：" + error.Message; }
        }

        void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try {
                string directory = runDirectory ?? logs;
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            } catch (Exception error) { FooterText.Text = "无法打开目录：" + error.Message; }
        }

        void OpenRepo(object sender, MouseButtonEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://github.com/Qi-2007/90HXWindowsUnlock") { UseShellExecute = true });
        }

        void ShowPreview(string state)
        {
            EnvironmentText.Text = "界面预览 · 示例数据";
            LinkText.Text = state == "success" ? "Gen2 ×16" : "待读取";
            ComputeText.Text = GraphicsText.Text = state == "success" ? "已解锁" : "无法确认";
            UnlockReadDetail.Text = state == "success" ? "示例快照 · 上游桥 Gen2 ×16 · 功能依据寄存器值，未进行负载测试。" : "示例数据 · 本次尚未取得解锁后的有效快照。";
            GpuText.Text = "NVIDIA CMP 90HX";
            PnpText.Text = "已停用 · Code 22";
            DriverText.Text = "运行中";
            UpdatedText.Text = "示例数据 · 未读取硬件";
            RefreshButton.IsEnabled = false;
            SetBadge("显卡已停用", "#FFF5E6", "#995E0C");
            SetBusy(state == "running");
            activeLabel = "解锁";
            SessionText.Text = "预览记录 · 不执行硬件操作";
            AcceptOutput("[10:24:00] GUI_WORKFLOW_BEGIN mode=Unlock\r\n[10:24:01] DMA_BACKEND=kernel-physical-experiment\r\n[10:24:02] STAGE=BRIDGE_ECAM_PREFLIGHT\r\n[10:24:03] STAGE=INITIAL_DUAL_SBR\r\n[10:24:08] STAGE=COMPUTE\r\n");
            for (int i = 0; i < 24; i++) AppendLog("[示例日志] 等待当前阶段完成…\r\n");
            AppendLog("[示例日志] 水平滚动预览：GPU 状态寄存器 " + String.Join(" · ", Enumerable.Repeat("0x88888888",12)) + "\r\n");
            AcceptOutput("[10:25:26] STAGE=GRAPHICS\r\n");
            ElapsedText.Text = "已用时 01:26";
            FooterText.Text = "预览模式";
            if (state == "failure") {
                AcceptOutput("FAILED_STAGE=GRAPHICS\r\nDMA_RETAINED: keep 90HX disabled; cold boot before retrying.\r\n");
                PresentResult("Unlock", "解锁", 1);
                FooterText.Text = "预览模式 · 失败示例";
            } else if (state == "success") {
                AcceptOutput("FULL_UNLOCK_VERIFIED_AFTER_NVIDIA_REATTACH\r\n");
                PresentResult("Unlock", "解锁", 0);
                PnpText.Text = "正常 · Code 0";
                SetBadge("设备就绪", "#E8F6EE", "#1D7546");
                FooterText.Text = "预览模式 · 成功示例";
            }
        }

        void SavePreview()
        {
            try {
                UpdateLayout();
                FrameworkElement content = (FrameworkElement)Content;
                if (LogBox.ActualHeight < 80 || LogBox.ActualWidth < 250)
                    throw new InvalidOperationException("Log viewport is too small: " + LogBox.ActualWidth + " x " + LogBox.ActualHeight);
                if (CheckButton.IsEnabled || UnlockButton.IsEnabled || TaskManagerButton.IsEnabled || PowerManagerButton.IsEnabled || ReadUnlockButton.IsEnabled || PrepareDriverButton.IsEnabled)
                    throw new InvalidOperationException("Preview must not enable workflows.");
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                    (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(App.SnapshotPath));
                using (FileStream output = File.Create(App.SnapshotPath)) encoder.Save(output);
                Application.Current.Shutdown(0);
            } catch (Exception error) {
                File.WriteAllText(App.SnapshotPath + ".error.txt", error.ToString());
                Application.Current.Shutdown(1);
            }
        }
    }
}
