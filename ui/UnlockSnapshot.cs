using System;
using System.Linq;
using System.Web.Script.Serialization;

namespace CMP90HX.Control
{
    public sealed class UnlockSnapshot
    {
        public sealed class Link { public uint? Status { get; set; } }
        public sealed class Register
        {
            public uint Offset { get; set; }
            public uint? Value { get; set; }
            public string Error { get; set; }
        }
        public int Schema { get; set; }
        public uint DeviceId { get; set; }
        public uint GpuBdf { get; set; }
        public string TimestampUtc { get; set; }
        public Link Gpu { get; set; }
        public Link Bridge { get; set; }
        public Register[] Registers { get; set; }

        public static UnlockSnapshot Parse(string json)
        {
            var value = new JavaScriptSerializer().Deserialize<UnlockSnapshot>(json);
            if (value == null || value.Schema != 1 || value.DeviceId != 0x220d10de)
                throw new FormatException("快照格式或显卡型号不匹配。");
            return value;
        }
        public static string DescribeLink(Link link)
        {
            if (link == null || !link.Status.HasValue || link.Status >= 0xffff) return "无法读取";
            uint status = link.Status.Value, speed = status & 15, width = (status >> 4) & 63;
            if (speed < 1 || speed > 6 || width == 0) return "未知链路";
            return "Gen" + speed + " ×" + width + ((status & 0x800) != 0 ? " · 训练中" : "");
        }
        public string FunctionState(params uint[] pairs)
        {
            bool mismatch = false;
            for (int i = 0; i < pairs.Length; i += 2) {
                var matches = (Registers ?? new Register[0]).Where(r => r != null && r.Offset == pairs[i]).ToArray();
                if (matches.Length != 1 || matches[0].Error != null || !matches[0].Value.HasValue || matches[0].Value == uint.MaxValue || (matches[0].Value & 0xffff0000) == 0xbadf0000)
                    return "无法确认";
                if (matches[0].Value != pairs[i + 1]) mismatch = true;
            }
            return mismatch ? "未达解锁值" : "已解锁";
        }
    }
}
