using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CMP90HX.Control {
    internal static class Onboarding {
        internal static void OpenPage(string url) {
            ValidateUrl(url);
            Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});
        }
        internal static bool OpenFirstTime() {
            string directory=Path.Combine(AppData.Root,"Onboarding");
            return OpenFirstTime(directory,OnboardingPolicy.Url,OpenPage);
        }
        // Keep this per guide URL; upgrades using the same guide do not reopen it.
        internal static bool OpenFirstTime(string directory,string url,Action<string> open) {
            ValidateUrl(url);
            string hash;
            using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","");
            string marker=Path.Combine(directory,hash+".opened");
            if(File.Exists(marker))return false;
            open(url);
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker,url,Encoding.UTF8);
            return true;
        }
        static void ValidateUrl(string url) {
            Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || (uri.Scheme!=Uri.UriSchemeHttps && uri.Scheme!=Uri.UriSchemeHttp))
                throw new ArgumentException("文档地址必须为 HTTP 或 HTTPS 网页。");
        }
    }
}
