using System;
using System.ComponentModel;
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
    internal sealed class PowerWindow : Window
    {
        readonly RuntimePaths paths;readonly string session;readonly bool preview;
        readonly TextBlock state=new TextBlock(),sample=new TextBlock(),detail=new TextBlock(),thresholdLabel=new TextBlock();
        readonly Slider threshold=new Slider {Minimum=1,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,Value=15};
        readonly TextBox idle=new TextBox {Text="10",Width=60},apps=new TextBox {Height=48,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true};
        readonly TextBox logBox=new TextBox();
        readonly Button enable,disable;readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
        bool busy,refreshing,closed;
        internal PowerWindow(MainWindow parent,RuntimePaths paths,string session,bool preview=false)
        {
            this.paths=paths;this.session=session;this.preview=preview;
            Title="空闲省电";Width=730;Height=820;MinWidth=660;MinHeight=760;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=parent.FontFamily;FontSize=14;UseLayoutRounding=true;
            Background=new SolidColorBrush(Color.FromRgb(244,246,249));
            var buttonStyle=(Style)parent.FindResource("ActionButton");var cardStyle=(Style)parent.FindResource("Card");
            var grid=new Grid {Margin=new Thickness(24)};
            for(int i=0;i<3;i++) grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            var heading=new StackPanel {Margin=new Thickness(0,0,0,18)};
            heading.Children.Add(new TextBlock {Text="空闲省电",FontSize=24,FontWeight=FontWeights.SemiBold});
            heading.Children.Add(new TextBlock {Text="空闲时限制到 P8，有负载时恢复 NVIDIA 自动性能策略",Foreground=Brushes.SlateGray,Margin=new Thickness(0,6,0,0)});
            grid.Children.Add(heading);
            var status=new StackPanel();state.FontSize=18;state.FontWeight=FontWeights.SemiBold;state.Text="正在读取后台状态…";status.Children.Add(state);
            sample.Margin=new Thickness(0,8,0,0);sample.TextWrapping=TextWrapping.Wrap;status.Children.Add(sample);
            detail.FontSize=12;detail.Foreground=Brushes.SlateGray;detail.Margin=new Thickness(0,8,0,0);detail.TextWrapping=TextWrapping.Wrap;detail.MaxHeight=44;detail.TextTrimming=TextTrimming.CharacterEllipsis;status.Children.Add(detail);
            var statusCard=new Border {Style=cardStyle,Child=status,Margin=new Thickness(0,0,0,14)};Grid.SetRow(statusCard,1);grid.Children.Add(statusCard);
            var controls=new StackPanel();
            thresholdLabel.Text="GPU 负载达到 15% 时恢复自动性能";controls.Children.Add(thresholdLabel);
            threshold.Margin=new Thickness(0,10,0,14);threshold.ValueChanged+=(s,e)=>thresholdLabel.Text="GPU 负载达到 "+(int)threshold.Value+"% 时恢复自动性能";controls.Children.Add(threshold);
            var idleRow=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,14)};
            idleRow.Children.Add(new TextBlock {Text="连续空闲等待",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)});idleRow.Children.Add(idle);
            idleRow.Children.Add(new TextBlock {Text="秒后进入 P8",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});controls.Children.Add(idleRow);
            controls.Children.Add(new TextBlock {Text="全速应用例外（进程名，用逗号或换行分隔）",Margin=new Thickness(0,0,0,6)});controls.Children.Add(apps);
            controls.Children.Add(new TextBlock {Text="检测到视频负载时自动解除 P8 限制。解锁期间暂停，恢复后重新检测显卡。",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(0,10,0,14)});
            var actions=new StackPanel {Orientation=Orientation.Horizontal};
            enable=new Button {Content="启用 / 保存省电设置",Style=buttonStyle,Margin=new Thickness(0,0,12,0)};
            disable=new Button {Content="停用并恢复自动性能",Style=buttonStyle};
            enable.Click+=async(s,e)=>await Operate(true);disable.Click+=async(s,e)=>await Operate(false);actions.Children.Add(enable);actions.Children.Add(disable);controls.Children.Add(actions);
            var controlsCard=new Border {Style=cardStyle,Child=controls,Margin=new Thickness(0,0,0,14)};Grid.SetRow(controlsCard,2);grid.Children.Add(controlsCard);
            logBox.IsReadOnly=true;logBox.FontFamily=new FontFamily("Cascadia Mono, Consolas");logBox.FontSize=12;logBox.Padding=new Thickness(12);logBox.BorderThickness=new Thickness(0);
            logBox.Resources.Add(typeof(ScrollViewer),parent.FindResource("LogScrollViewer"));
            logBox.Background=new SolidColorBrush(Color.FromRgb(16,26,42));logBox.Foreground=new SolidColorBrush(Color.FromRgb(215,228,244));logBox.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;logBox.TextWrapping=TextWrapping.Wrap;
            Grid.SetRow(logBox,3);grid.Children.Add(logBox);
            var surface=new Grid {Background=Background};surface.Children.Add(grid);
            surface.Children.Add(new ContentControl {Content="Watermark",ContentTemplate=(DataTemplate)parent.FindResource("WatermarkTemplate"),IsHitTestVisible=false,Focusable=false,ClipToBounds=true,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch});
            Content=surface;
            Loaded+=async(s,e)=> {
                if(preview) {
                    state.Text="空闲省电 · P8 限制";sample.Text="当前 P8   ·   GPU 0%   ·   视频 0%   ·   28.6 W";detail.Text="界面预览 · 示例数据，后台以 SYSTEM 身份运行，关闭 GUI 后继续控制。";
                    SetBusy(false);Log("空闲持续 10 秒，已限制到 P8。\n负载达到阈值或例外应用启动时，恢复自动性能。");
                    if(App.SnapshotPath!=null) await Dispatcher.InvokeAsync(new Action(SavePreview),DispatcherPriority.ApplicationIdle);
                } else {
                    try {var settings=PowerStorage.ReadSettings();threshold.Value=settings.Threshold;idle.Text=settings.IdleSeconds.ToString();apps.Text=settings.FullSpeedApps;}catch(Exception error) {Log(error.Message);}
                    Log("先退出 Inspector 的 Multi Display Power Saver。启用内置省电后会停用它的登录启动任务，保留原程序和配置。\n后台任务独立运行，关闭本窗口不会停止省电。");
                    await Refresh();timer.Start();
                }
            };
            timer.Tick+=async(s,e)=>{if(!busy) await Refresh();};Closed+=(s,e)=>{closed=true;timer.Stop();};
            Closing+=(s,e)=>{if(busy) {e.Cancel=true;detail.Text="设置操作仍在运行，请等待完成。";}};
        }
        void Log(string value)
        {
            if(!Dispatcher.CheckAccess()) {if(!closed) Dispatcher.BeginInvoke(new Action(()=>Log(value)));return;}
            logBox.AppendText(value+"\r\n");if(logBox.Text.Length>50000) logBox.Text=logBox.Text.Substring(logBox.Text.Length-40000);logBox.ScrollToEnd();
        }
        void SetBusy(bool value)
        {
            busy=value;bool active=!value && !preview;
            enable.IsEnabled=disable.IsEnabled=threshold.IsEnabled=idle.IsEnabled=apps.IsEnabled=active;
        }
        async Task Operate(bool on)
        {
            if(busy || preview) return;SetBusy(true);
            try {
                int delay;if(!Int32.TryParse(idle.Text,out delay)) throw new IOException("空闲等待请输入整数秒数。");
                var settings=new PowerSettings {Enabled=on,Threshold=(int)threshold.Value,IdleSeconds=delay,FullSpeedApps=apps.Text};settings.Validate();
                string directory=Path.Combine(session,"power-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                await Task.Run(()=> {
                    using(var writer=new StreamWriter(Path.Combine(directory,"settings.log"),false,Encoding.UTF8)) {
                        writer.AutoFlush=true;Action<string> log=line=>{writer.WriteLine(line);Log(line);};
                        try {if(on) PowerTasks.Enable(paths,settings,log);else PowerTasks.Disable(log);}
                        catch(Exception error) {log("FAILED: "+error.Message);throw;}
                    }
                });
                Log(on?"内置省电已启用。":"内置省电已停用。");
            } catch(Exception error) {Log(error.Message);}
            finally {SetBusy(false);await Refresh();}
        }
        async Task Refresh()
        {
            if(refreshing || closed || preview) return;refreshing=true;
            try {
                var data=await Task.Run(()=>new {Settings=PowerStorage.ReadSettings(),Status=PowerStorage.ReadStatus(),Task=TaskManagement.Query(TaskManagement.PowerName)});
                if(closed) return;
                bool live=data.Status!=null && (DateTime.UtcNow-data.Status.HeartbeatUtc).TotalSeconds<5;
                state.Text=live?data.Status.Policy:data.Settings.Enabled?"等待后台启动 / 恢复":"省电未启用";
                var current=live?data.Status.Sample:null;
                sample.Text=current!=null?"当前 P"+current.Pstate+"   ·   GPU "+current.Gpu+"%   ·   视频 "+current.Video+"%   ·   "+(current.Watts.HasValue?current.Watts.Value.ToString("0.0")+" W":"功耗不可用"):"当前显卡数据待读取";
                detail.Text=live?data.Status.Detail:"后台任务："+data.Task.Text;
                detail.ToolTip=detail.Text;
            } catch(Exception error) {state.Text="省电状态读取失败";detail.Text=error.Message;}
            finally {refreshing=false;}
        }
        void SavePreview()
        {
            try {
                UpdateLayout();var content=(FrameworkElement)Content;
                if(logBox.ActualHeight<80) throw new IOException("Power log viewport too small.");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(Path.GetDirectoryName(App.SnapshotPath));using(var stream=File.Create(App.SnapshotPath)) encoder.Save(stream);
                Application.Current.Shutdown(0);
            } catch(Exception error) {File.WriteAllText(App.SnapshotPath+".error.txt",error.ToString());Application.Current.Shutdown(1);}
        }
    }
}
