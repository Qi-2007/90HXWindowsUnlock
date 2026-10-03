using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CMP90HX.Control
{
    internal sealed class ManagementWindow : Window
    {
        readonly bool taskMode,preview;
        readonly RuntimePaths paths;
        readonly string session;
        readonly TextBlock first=new TextBlock(),second=new TextBlock(),driver=new TextBlock(),notice=new TextBlock();
        readonly TextBox output=new TextBox();
        readonly List<Button> buttons=new List<Button>();
        readonly Style buttonStyle,cardStyle;
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(3)};
        Button install,uninstall,trigger;
        bool busy,refreshing,closed;
        internal ManagementWindow(MainWindow parent,bool taskMode,RuntimePaths paths,string session,bool preview=false)
        {
            this.taskMode=taskMode;this.paths=paths;this.session=session;this.preview=preview;
            buttonStyle=(Style)parent.FindResource("ActionButton");cardStyle=(Style)parent.FindResource("Card");
            Title=taskMode?"计划任务管理":"驱动管理";
            Width=660;Height=700;MinWidth=580;MinHeight=620;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=new SolidColorBrush(Color.FromRgb(244,246,249));
            FontFamily=parent.FontFamily;FontSize=14;UseLayoutRounding=true;
            var grid=new Grid();
            grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            var heading=new StackPanel {Margin=new Thickness(0,0,0,20)};
            heading.Children.Add(new TextBlock {Text=Title,FontSize=24,FontWeight=FontWeights.SemiBold});
            heading.Children.Add(new TextBlock {Text=taskMode?"以 SYSTEM 身份在开机和睡眠唤醒后运行":"系统根证书与 DMA 驱动服务",Foreground=Brushes.SlateGray,Margin=new Thickness(0,6,0,0)});
            grid.Children.Add(heading);
            var body=new StackPanel();
            body.Children.Add(Status(first));
            body.Children.Add(Status(second));
            body.Children.Add(Status(driver));
            notice.TextWrapping=TextWrapping.Wrap;notice.Foreground=Brushes.SlateGray;notice.FontSize=12;notice.Margin=new Thickness(0,8,0,12);
            notice.Text=taskMode?"安装时自动补齐证书和驱动，环境检查通过后注册。后台已解锁则跳过。":"证书安装到 LocalMachine / Root。已加载的驱动卸载后，重启生效。";
            body.Children.Add(notice);
            if(taskMode) {
                var taskActions=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(taskActions);
                install=Button(taskActions,"安装计划任务",()=>RegisterTask());
                uninstall=Button(taskActions,"卸载计划任务",()=>TaskManagement.Uninstall(currentLog));
                trigger=Button(taskActions,"手动触发",()=>TaskManagement.Run(currentLog));
            } else {
                var certActions=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(certActions);
                Button(certActions,"安装证书",()=>CertificateManager.Install(paths,currentLog));
                Button(certActions,"卸载证书",()=>CertificateManager.Uninstall(currentLog));
                var driverActions=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(driverActions);
                Button(driverActions,"安装驱动",()=> {
                    CertificateManager.RequireInstalled();paths.Validate(false,true);
                    DriverService.EnsureRunning(paths.DmaDriver,currentLog);
                });
                Button(driverActions,"卸载驱动",()=>DriverService.Uninstall(currentLog));
            }
            var card=new Border {Style=cardStyle,Child=body,Margin=new Thickness(0,0,0,16)};
            Grid.SetRow(card,1);grid.Children.Add(card);
            var logGrid=new Grid();
            logGrid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            logGrid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            var label=new DockPanel {Margin=new Thickness(0,0,0,10)};
            var open=new Button {Content="打开日志目录",Style=buttonStyle,Height=30,Padding=new Thickness(12,0,12,0),HorizontalAlignment=HorizontalAlignment.Right};
            open.Click+=(s,e)=> { try {Directory.CreateDirectory(paths.Logs);Process.Start(new ProcessStartInfo(paths.Logs) {UseShellExecute=true});} catch(Exception error) {Log(error.Message);} };
            DockPanel.SetDock(open,Dock.Right);label.Children.Add(open);
            label.Children.Add(new TextBlock {Text="操作记录",FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
            logGrid.Children.Add(label);
            output.IsReadOnly=true;output.FontFamily=new FontFamily("Cascadia Mono, Consolas");output.FontSize=12;
            output.Background=new SolidColorBrush(Color.FromRgb(16,26,42));output.Foreground=new SolidColorBrush(Color.FromRgb(215,228,244));
            output.Padding=new Thickness(12);output.BorderThickness=new Thickness(0);
            output.Resources.Add(typeof(ScrollViewer),parent.FindResource("LogScrollViewer"));
            output.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;output.HorizontalScrollBarVisibility=ScrollBarVisibility.Auto;
            Grid.SetRow(output,1);logGrid.Children.Add(output);
            Grid.SetRow(logGrid,2);grid.Children.Add(logGrid);
            var surface=new Grid {Background=Background};
            surface.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            surface.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            surface.Children.Add(new Border {Background=Background,Padding=new Thickness(24),Child=grid});
            surface.Children.Add(new ContentControl {
                Content="Watermark",ContentTemplate=(DataTemplate)parent.FindResource("WatermarkTemplate"),
                IsHitTestVisible=false,Focusable=false,ClipToBounds=true,
                HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch
            });
            var attribution=new TextBlock {
                Text="免费工具 · 禁止倒卖  |  Qi-2007/90HXWindowsUnlock",
                FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(24,0,24,12),
                ToolTip="项目来源：https://github.com/Qi-2007/90HXWindowsUnlock"
            };
            Grid.SetRow(attribution,1);surface.Children.Add(attribution);
            Content=surface;
            Loaded+=async(s,e)=> {
                if(preview) {
                    ShowPreview();
                    if(App.SnapshotPath!=null) await Dispatcher.InvokeAsync(new Action(SavePreview),DispatcherPriority.ApplicationIdle);
                } else {await Refresh();if(taskMode) timer.Start();}
            };
            timer.Tick+=async(s,e)=>{if(!busy) await Refresh();};
            Closing+=OnClosing;Closed+=(s,e)=>{closed=true;timer.Stop();};
        }
        Border Status(TextBlock text)
        {
            text.TextWrapping=TextWrapping.Wrap;text.FontWeight=FontWeights.SemiBold;
            return new Border {Background=new SolidColorBrush(Color.FromRgb(245,248,252)),CornerRadius=new CornerRadius(7),Padding=new Thickness(12),Margin=new Thickness(0,0,0,10),Child=text};
        }
        Button Button(Panel panel,string text,Action action)
        {
            var button=new Button {Content=text,Style=buttonStyle,Margin=new Thickness(0,0,10,10),Padding=new Thickness(15,0,15,0)};
            button.Click+=async(s,e)=>await Operate(text,action);
            panel.Children.Add(button);buttons.Add(button);return button;
        }
        void Log(string line)
        {
            if(!Dispatcher.CheckAccess()) { if(!closed) Dispatcher.BeginInvoke(new Action(()=>Log(line)));return; }
            output.AppendText(line+"\r\n");
            if(output.Text.Length>90000) output.Text=output.Text.Substring(output.Text.Length-70000);
            output.ScrollToEnd();
        }
        Action<string> currentLog;
        void RegisterTask()
        {
            EnvironmentSetup.Prepare(()=> {
                paths.Validate(true,false);CertificateManager.ValidateFiles(paths);RuntimePaths.RequireHash(paths.DmaDriver,RuntimePaths.DmaHash);
            },CertificateManager.Ready,()=>CertificateManager.Install(paths,currentLog),DriverService.Installed,
                ()=>DriverService.EnsureRunning(paths.DmaDriver,currentLog),()=> {
                    var platform=new WindowsWorkflowPlatform(paths,currentLog);
                    return new NativeWorkflow(paths,platform,currentLog).Execute("Environment",currentDirectory);
                },()=>TaskManagement.Register(paths,currentLog),currentLog);
        }
        string currentDirectory;
        async Task Operate(string label,Action action)
        {
            if(busy || preview) return;
            busy=true;foreach(var button in buttons) button.IsEnabled=false;
            output.Clear();notice.Text="正在"+label+"…";
            try {
                currentDirectory=Path.Combine(session,"management-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(currentDirectory);
                await Task.Run(()=> {
                    using(var writer=new StreamWriter(Path.Combine(currentDirectory,"management.log"),false,Encoding.UTF8)) {
                        writer.AutoFlush=true;
                        currentLog=line=>{lock(writer) writer.WriteLine(line);Log(line);};
                        currentLog("BEGIN "+label);
                        try { action();currentLog("SUCCESS "+label); }
                        catch(Exception error) {currentLog("FAILED: "+error.Message);throw;}
                        finally { currentLog=null; }
                    }
                });
                notice.Text=label+"已完成。";
            } catch(Exception error) {notice.Text=label+"失败："+error.Message;}
            finally {
                busy=false;await Refresh();
                // Keep the operation result in the log; state cards always reflect the system.
            }
        }
        async Task Refresh()
        {
            if(refreshing || closed || preview) return;
            refreshing=true;
            try {
                var state=await Task.Run(()=>new {Certificates=CertificateManager.Installed(),Driver=DriverService.CurrentState(),Task=taskMode?TaskManagement.Query():null});
                if(closed) return;
                if(taskMode) {
                    first.Text="计划任务："+state.Task.Text;
                    second.Text="触发器：开机和唤醒后立即开始，等待设备就绪";
                    driver.Text="根证书："+(state.Certificates[0] && state.Certificates[1]?"已安装":"未完整安装")+"   ·   DMA 驱动："+state.Driver;
                    install.IsEnabled=!busy;uninstall.IsEnabled=trigger.IsEnabled=!busy && state.Task.Exists;
                } else {
                    first.Text="Pikachu Test CA RSA："+(state.Certificates[0]?"已安装":"未安装")+"\n"+CertificateManager.Thumbprints[0];
                    second.Text="Pikachu Time Sub CA："+(state.Certificates[1]?"已安装":"未安装")+"\n"+CertificateManager.Thumbprints[1];
                    driver.Text="DMA 驱动："+state.Driver;
                    foreach(var button in buttons) button.IsEnabled=!busy;
                }
            } catch(Exception error) {notice.Text="状态读取失败："+error.Message;foreach(var button in buttons) button.IsEnabled=!busy;}
            finally {refreshing=false;}
        }
        void OnClosing(object sender,CancelEventArgs e) {if(busy) {e.Cancel=true;notice.Text="操作仍在运行，请等待完成。";} }
        void ShowPreview()
        {
            foreach(var button in buttons) button.IsEnabled=false;
            if(taskMode) {
                first.Text="计划任务：已安装 · 已启用\n上次运行：2026-10-03 16:20:00\n上次结果：0x00000000";
                second.Text="触发器：开机和唤醒后立即开始，等待设备就绪";
                driver.Text="根证书：已安装   ·   DMA 驱动：运行中";
                notice.Text="安装时自动补齐证书和驱动，环境检查通过后注册。";
            } else {
                first.Text="Pikachu Test CA RSA：已安装\n"+CertificateManager.Thumbprints[0];
                second.Text="Pikachu Time Sub CA：未安装\n"+CertificateManager.Thumbprints[1];
                driver.Text="DMA 驱动：已安装 · 操作时启动";
                notice.Text="证书安装到 LocalMachine / Root。已加载的驱动卸载后，重启生效。";
            }
            Log("界面预览 · 示例状态，不读取硬件，不安装证书或计划任务。");
        }
        void SavePreview()
        {
            try {
                UpdateLayout();var content=(FrameworkElement)Content;
                if(output.ActualHeight<80) throw new IOException("Management log viewport too small.");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
                bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(App.SnapshotPath));using(var stream=File.Create(App.SnapshotPath)) encoder.Save(stream);
                Application.Current.Shutdown(0);
            } catch(Exception error) {File.WriteAllText(App.SnapshotPath+".error.txt",error.ToString());Application.Current.Shutdown(1);}
        }
    }
}
