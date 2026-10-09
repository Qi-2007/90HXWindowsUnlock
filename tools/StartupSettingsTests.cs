using System;
using System.IO;
using CMP90HX.Control;

static class StartupSettingsTests
{
    static void Require(bool condition,string message)
    {if(!condition) throw new Exception(message);}
    static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"CMP90HX-startup-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path=Path.Combine(root,"settings.json");
        try {
            Require(!StartupSettings.AutoUnlockDisabled(false,root),"missing config retains startup reads");
            Require(!File.Exists(path),"reading settings must not create a config");
            File.WriteAllText(path,"{\"Power\":{\"Enabled\":true},\"Other\":42}");
            Require(!StartupSettings.AutoUnlockDisabled(false,root),"existing settings without flag retain defaults");
            File.WriteAllText(path,"{\"DisableAutoUnlock\":true,\"Power\":{\"Enabled\":true},\"Other\":42}");
            string saved=File.ReadAllText(path);
            Require(StartupSettings.AutoUnlockDisabled(false,root),"config disables automatic hardware actions");
            Require(File.ReadAllText(path)==saved,"unrelated configuration is preserved");
            File.WriteAllText(path,"{\"DisableAutoUnlock\":false}");
            Require(!StartupSettings.AutoUnlockDisabled(false,root),"false restores default startup reads");
            Require(StartupSettings.AutoUnlockDisabled(true,root),"command line overrides config false");
            foreach(string invalid in new[]{"null","broken JSON","{\"DisableAutoUnlock\":\"false\"}","{\"DisableAutoUnlock\":null}","{\"DisableAutoUnlock\":1}"}) {
                File.WriteAllText(path,invalid);
                Require(StartupSettings.AutoUnlockDisabled(true,root),"recovery switch bypasses damaged config");
                bool rejected=false;
                try {StartupSettings.AutoUnlockDisabled(false,root);} catch(Exception) {rejected=true;}
                Require(rejected,"invalid settings must not silently enable automatic operations");
            }
            Console.WriteLine("STARTUP_SETTINGS_TESTS_PASSED: defaults, CLI override, config disable, preservation and invalid settings.");
        } finally {Directory.Delete(root,true);}
    }
}
