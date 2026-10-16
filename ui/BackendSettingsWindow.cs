using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CMP90HX.Control {
    internal sealed class BackendSettingsWindow : Window {
        internal BackendSettingsWindow(Window owner,bool preview=false) {
            if(!preview)Owner=owner;Title="设置";Width=560;Height=700;MinWidth=500;MinHeight=620;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=owner.FontFamily;FontSize=14;UseLayoutRounding=true;
            Background=new SolidColorBrush(Color.FromRgb(244,246,249));
            var state=preview?new ControlState():BackendClient.Read();var options=state.Options;
            Foreground=owner.Foreground;var root=new DockPanel();
            var surface=new Grid {Background=(Brush)FindResource("WindowBackgroundBrush")};surface.Children.Add(root);
            surface.Children.Add(new ContentControl {Content="Watermark",ContentTemplate=(DataTemplate)owner.FindResource("WatermarkTemplate"),IsHitTestVisible=false,Focusable=false,ClipToBounds=true,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch});Content=surface;
            var heading=new StackPanel {Margin=new Thickness(24,20,24,20)};
            heading.Children.Add(new TextBlock {Text="控制台设置",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(24,37,54))});
            heading.Children.Add(new TextBlock {Text="解锁方案与后台自动操作",Foreground=Brushes.SlateGray,Margin=new Thickness(0,6,0,0)});
            var header=new Border {Background=(Brush)FindResource("WhiteSurfaceBrush"),BorderBrush=new SolidColorBrush(Color.FromRgb(227,232,239)),BorderThickness=new Thickness(0,0,0,1),Child=heading};
            DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
            var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(24,0,24,20)};
            var cancel=new Button {Content="取消",Style=(Style)owner.FindResource("ActionButton"),Width=80,Margin=new Thickness(0,0,10,0)};cancel.Click+=(s,e)=>Close();footer.Children.Add(cancel);
            var save=new Button {Content="保存设置",Style=(Style)owner.FindResource("PrimaryButton"),Width=120,Height=38};footer.Children.Add(save);
            DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var panel=new StackPanel();
            var sections=new StackPanel {Margin=new Thickness(24,20,24,20)};
            sections.Children.Add(new Border {Style=(Style)owner.FindResource("Card"),Child=panel,Margin=new Thickness(0,0,0,16)});
            panel.Children.Add(new TextBlock {Text="解锁方式",Style=(Style)owner.FindResource("SectionTitle"),Margin=new Thickness(0,0,0,10)});
            string currentBackend=options.Backend;
            var backendRow=new DockPanel();var backendLabel=new TextBlock {Text="当前："+currentBackend,VerticalAlignment=VerticalAlignment.Center};
            var backend=new Button {Content="切换为 "+(currentBackend=="Native"?"Session":"Native"),Style=(Style)owner.FindResource("ActionButton"),Height=38,Padding=new Thickness(12,0,12,0)};DockPanel.SetDock(backend,Dock.Right);backendRow.Children.Add(backend);backendRow.Children.Add(backendLabel);panel.Children.Add(backendRow);
            panel.Children.Add(new TextBlock {Text="切换方式会卸载旧方式的解锁驱动；新驱动需在驱动管理中手动安装。已加载的 Native 驱动可能需重启后完成卸载。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,FontSize=12,Margin=new Thickness(0,10,0,14)});
            var conservative=new CheckBox {Content="保守兼容模式（较慢，仅 Native 生效）",IsChecked=options.ConservativeTiming,Margin=new Thickness(0,4,0,14)};panel.Children.Add(conservative);
            conservative.IsEnabled=options.Backend=="Native";
            var automatic=new StackPanel();
            sections.Children.Add(new Border {Style=(Style)owner.FindResource("Card"),Child=automatic});
            automatic.Children.Add(new TextBlock {Text="后台自动操作",Style=(Style)owner.FindResource("SectionTitle"),Margin=new Thickness(0,0,0,8)});
            var startup=Option(automatic,"开机自启后台",options.StartOnBoot);
            automatic.Children.Add(new TextBlock {Text="以 SYSTEM 身份在开机时运行。关闭控制台后继续执行省电和唤醒操作。",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(20,0,0,8)});
            var unlock=Option(automatic,"开机启动时自动解锁",options.UnlockOnStartup);
            var resume=Option(automatic,"睡眠唤醒后自动解锁",options.UnlockOnResume);
            automatic.Children.Add(new TextBlock {Text="解锁状态仅手动刷新，解锁完成后验证一次。",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(0,8,0,0)});
            var detail=new TextBlock {Text=state.WakeNotifications?"后台直接监听系统唤醒通知。关闭前台后继续运行。":state.WakeNotificationError??"唤醒通知尚未注册。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,FontSize=12,Margin=new Thickness(0,14,0,0)};automatic.Children.Add(detail);
            root.Children.Add(new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=sections});
            if(preview){backend.IsEnabled=save.IsEnabled=false;Loaded+=(s,e)=> {if(App.SnapshotPath!=null)Dispatcher.BeginInvoke(new Action(SavePreview),System.Windows.Threading.DispatcherPriority.ApplicationIdle);};return;}
            bool saving=false;Closing+=(s,e)=>{if(saving)e.Cancel=true;};
            backend.Click+=async(s,e)=> {
                if(saving)return;
                saving=true;save.IsEnabled=cancel.IsEnabled=backend.IsEnabled=false;
                string previous=currentBackend;
                detail.Text="正在卸载旧模式的解锁驱动并保存新模式…";
                try {
                    await Task.Run(()=>BackendClient.Require("SwitchBackend",previous,null));
                    var updated=BackendClient.Read();currentBackend=updated.Options.Backend;
                    if(currentBackend==previous)throw new System.IO.IOException("后台未确认模式切换，请查看运行日志。");
                    conservative.IsEnabled=currentBackend=="Native";
                    detail.Text="已切换为 "+currentBackend+"，配置已保存。请到驱动管理手动安装对应解锁驱动。已加载的旧 Native 驱动需重启后释放。";
                }catch(Exception error){detail.Text="切换失败："+error.Message;}
                finally {
                    try {currentBackend=BackendClient.Read().Options.Backend;}catch(Exception){}
                    backendLabel.Text="当前："+currentBackend;backend.Content="切换为 "+(currentBackend=="Native"?"Session":"Native");
                    conservative.IsEnabled=currentBackend=="Native";
                    saving=false;save.IsEnabled=cancel.IsEnabled=backend.IsEnabled=true;
                }
            };
            save.Click+=async(s,e)=> {
                saving=true;save.IsEnabled=cancel.IsEnabled=backend.IsEnabled=false;
                try {
                    var value=new ControlOptions {Backend=BackendClient.Read().Options.Backend,StartOnBoot=startup.IsChecked==true,ConservativeTiming=conservative.IsChecked==true,UnlockOnStartup=unlock.IsChecked==true,UnlockOnResume=resume.IsChecked==true,ReadUnlockOnConnect=false};
                    await Task.Run(()=>BackendClient.Require("SetOptions",ControlMemory.Json().Serialize(value),null));saving=false;Close();
                }catch(Exception error){detail.Text=error.Message;saving=false;save.IsEnabled=cancel.IsEnabled=backend.IsEnabled=true;}
            };
        }
        void SavePreview() {
            try {
                UpdateLayout();var content=(FrameworkElement)Content;
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(App.SnapshotPath));using(var stream=System.IO.File.Create(App.SnapshotPath))encoder.Save(stream);Application.Current.Shutdown(0);
            }catch(Exception error){System.IO.File.WriteAllText(App.SnapshotPath+".error.txt",error.ToString());Application.Current.Shutdown(1);}
        }
        static CheckBox Option(Panel panel,string text,bool value) {var item=new CheckBox {Content=text,IsChecked=value,Margin=new Thickness(0,6,0,6)};panel.Children.Add(item);return item;}
    }
}
