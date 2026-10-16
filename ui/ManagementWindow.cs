using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CMP90HX.Control {
    internal sealed class ManagementWindow : Window {
        readonly bool preview;
        readonly TextBlock reader=new TextBlock(),native=new TextBlock(),sessionDriver=new TextBlock(),notice=new TextBlock();
#if ENABLE_ROOT_CERTIFICATES
        readonly TextBlock certificate=new TextBlock();
#endif
        readonly TextBox output=new TextBox();
        readonly List<Button> buttons=new List<Button>();
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(3)};
        readonly Button mode;readonly TextBlock modeLabel=new TextBlock();
        bool busy,closed,refreshing;
        string selected="Native";
        internal ManagementWindow(MainWindow parent,RuntimePaths paths,string session,bool preview=false) {
            this.preview=preview;if(!preview)Owner=parent;Title="驱动管理";Width=700;Height=790;MinWidth=630;MinHeight=710;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=parent.FontFamily;FontSize=14;UseLayoutRounding=true;
            Background=new SolidColorBrush(Color.FromRgb(244,246,249));
            Foreground=parent.Foreground;var grid=new Grid {Margin=new Thickness(24),Background=Background};
            var surface=new Grid {Background=Background};surface.Children.Add(grid);
            surface.Children.Add(new ContentControl {Content="Watermark",ContentTemplate=(DataTemplate)parent.FindResource("WatermarkTemplate"),IsHitTestVisible=false,Focusable=false,ClipToBounds=true,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch});Content=surface;
            grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());
            var heading=new StackPanel {Margin=new Thickness(0,0,0,18)};
            heading.Children.Add(new TextBlock {Text="驱动管理",FontSize=24,FontWeight=FontWeights.SemiBold});
            heading.Children.Add(new TextBlock {Text="读取与解锁驱动均需手动安装",Foreground=Brushes.SlateGray,Margin=new Thickness(0,6,0,0)});grid.Children.Add(heading);
            foreach(string key in new[]{"ActionButton","PrimaryButton","SectionTitle","Card","WatermarkTemplate"})Resources[key]=parent.FindResource(key);
            var body=new StackPanel();var modeRow=new DockPanel {Margin=new Thickness(0,0,0,14)};modeLabel.VerticalAlignment=VerticalAlignment.Center;mode=new Button {Style=(Style)parent.FindResource("ActionButton"),HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,0,12,0),Content="更改方式",Height=32};
            mode.Click+=async(s,e)=>{if(busy || preview)return;try {new BackendSettingsWindow(this).ShowDialog();await Refresh();}catch(Exception error){notice.Text=error.Message;}};DockPanel.SetDock(mode,Dock.Right);modeRow.Children.Add(mode);modeRow.Children.Add(modeLabel);body.Children.Add(modeRow);buttons.Add(mode);
            var statuses=new List<TextBlock>{reader,native,sessionDriver};
#if ENABLE_ROOT_CERTIFICATES
            statuses.Add(certificate);
#endif
            foreach(var item in statuses) {item.TextWrapping=TextWrapping.Wrap;item.Margin=new Thickness(0,0,0,10);body.Children.Add(item);}
            notice.Text="Session 安装仅加入驱动仓库，解锁时会临时接管，结束后自动恢复 NVIDIA驱动。";notice.TextWrapping=TextWrapping.Wrap;notice.Foreground=Brushes.SlateGray;notice.FontSize=12;notice.Margin=new Thickness(0,4,0,12);body.Children.Add(notice);
            var installRow=new WrapPanel();body.Children.Add(installRow);
            AddButton(installRow,"安装读取驱动","ReaderInstall",parent);
            var unlockInstall=AddButton(installRow,"安装解锁驱动",null,parent);
            unlockInstall.Click+=async(s,e)=> {
                if(busy || preview)return;
                try {selected=BackendClient.Read().Options.Backend;}catch(Exception error){notice.Text=error.Message;return;}
                string expected=selected;
                if(MessageBox.Show(this,"确认安装当前选择的 "+expected+" 解锁驱动？\n"+(expected=="Native"?"安装前将再次检查内核 DMA 保护；开启时无法安装。":"当前仅安装驱动包，不会替换 NVIDIA 驱动。"),"确认安装解锁驱动",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)await Operate("UnlockDriverInstall",expected);
            };
            var removeRow=new WrapPanel();body.Children.Add(removeRow);AddButton(removeRow,"卸载读取驱动","ReaderRemove",parent);AddButton(removeRow,"卸载解锁驱动","UnlockDriverRemove",parent);
#if ENABLE_ROOT_CERTIFICATES
            var certRow=new WrapPanel();body.Children.Add(certRow);AddButton(certRow,"安装证书","CertificatesInstall",parent);AddButton(certRow,"卸载证书","CertificatesRemove",parent);
#endif
            var card=new Border {Style=(Style)parent.FindResource("Card"),Child=body,Margin=new Thickness(0,0,0,16)};Grid.SetRow(card,1);grid.Children.Add(card);
            output.IsReadOnly=true;output.FontFamily=new FontFamily("Cascadia Mono, Consolas");output.FontSize=12;output.Padding=new Thickness(0);output.BorderThickness=new Thickness(0);
            output.Background=new SolidColorBrush(Color.FromRgb(16,26,42));output.Foreground=new SolidColorBrush(Color.FromRgb(215,228,244));
            output.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;output.HorizontalScrollBarVisibility=ScrollBarVisibility.Auto;output.Resources.Add(typeof(ScrollViewer),parent.FindResource("LogScrollViewer"));var logFrame=new Border {Background=output.Background,CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Child=output};Grid.SetRow(logFrame,2);grid.Children.Add(logFrame);
            Loaded+=async(s,e)=> {if(preview){Apply(new ControlState {ReaderDriverState="未安装",NativeDriverState="运行中",SessionDriverState="未安装"});foreach(var b in buttons)b.IsEnabled=false;if(App.SnapshotPath!=null)await Dispatcher.InvokeAsync(new Action(SavePreview),DispatcherPriority.ApplicationIdle);}else {await Refresh();timer.Start();}};
            timer.Tick+=async(s,e)=>{if(!busy)await Refresh();};Closed+=(s,e)=>{closed=true;timer.Stop();};Closing+=(s,e)=>{if(busy)e.Cancel=true;};
        }
        Button AddButton(Panel panel,string label,string command,MainWindow parent) {
            var button=new Button {Content=label,Style=(Style)parent.FindResource("ActionButton"),Padding=new Thickness(14,0,14,0),Margin=new Thickness(0,0,10,10)};
            if(command!=null)button.Click+=async(s,e)=>await Operate(command,null);
            panel.Children.Add(button);buttons.Add(button);return button;
        }
        void Apply(ControlState state) {selected=state.Options.Backend;modeLabel.Text="当前解锁方式："+selected;reader.Text="读取驱动（Read）："+(state.ReaderDriverState??"待读取");native.Text="Native 解锁驱动："+(state.NativeDriverState??"待读取");sessionDriver.Text="Session 解锁驱动："+ShortSessionState(state.SessionDriverState);
#if ENABLE_ROOT_CERTIFICATES
            certificate.Text="签名根证书："+(state.Certificates.Length>0 && state.Certificates[0]?"已安装":"未安装");
#endif
        }
        static string ShortSessionState(string value) {return String.IsNullOrWhiteSpace(value)?"待读取":value.Split(new[]{'·','\r','\n'},StringSplitOptions.RemoveEmptyEntries)[0].Trim();}
        async Task Refresh() {if(refreshing || closed || preview)return;refreshing=true;try {var state=await Task.Run(()=>BackendClient.Read());if(!closed)Apply(state);}catch(Exception error){notice.Text=error.Message;}finally{refreshing=false;}}
        async Task Operate(string command,string data) {
            if(busy || preview)return;busy=true;foreach(var b in buttons)b.IsEnabled=false;output.Clear();notice.Text="操作正在执行…";
            try {await Task.Run(()=>BackendClient.Require(command,data,line=>Dispatcher.BeginInvoke(new Action(()=>{if(!closed){output.AppendText(line+"\r\n");output.ScrollToEnd();}}))));notice.Text="操作已完成。已加载的 Native / Read 驱动卸载后需要重启生效。";}
            catch(Exception error){notice.Text=error.Message;output.AppendText(error.Message+"\r\n");}
            finally{busy=false;foreach(var b in buttons)b.IsEnabled=true;await Refresh();}
        }
        void SavePreview() {
            try {UpdateLayout();var content=(FrameworkElement)Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(Path.GetDirectoryName(App.SnapshotPath));using(var stream=File.Create(App.SnapshotPath))encoder.Save(stream);Application.Current.Shutdown(0);}
            catch(Exception error){File.WriteAllText(App.SnapshotPath+".error.txt",error.ToString());Application.Current.Shutdown(1);}
        }
    }
}
