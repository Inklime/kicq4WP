using System;

public class ContactInfo
{
    public string Uin { get; set; }
    public uint Status { get; set; }
    public uint OnlineTime { get; set; }   // секунды онлайн
    public uint SignonTime { get; set; }   // unix timestamp
    public uint MemberSince { get; set; }   // unix timestamp
    public uint ExternalIp { get; set; }
    public ushort UserClass { get; set; }
    public uint DcInternalIp { get; set; }
    public ushort DcPort { get; set; }
    public byte DcType { get; set; }

    // Отформатированные строки для UI
    public string StatusText
    {
        get
        {
            ushort s = (ushort)(Status & 0xFFFF);
            switch (s)
            {
                case 0x0000: return "В сети";
                case 0x0001: return "Отошел";
                case 0x0002: return "Не беспокоить";
                case 0x0004: return "Недоступен";
                case 0x0010: return "Занят";
                case 0x0020: return "Готов поболтать";
                case 0x0100: return "Невидимый";
                case 0x3000: return "Злой";
                case 0x4000: return "Депрессия";
                case 0x5000: return "Дома";
                case 0x6000: return "Работа";
                case 0x2001: return "Кушаю";
                default: return "Статус 0x" + s.ToString("X4");
            }
        }
    }

    public string OnlineTimeText
    {
        get
        {
            if (OnlineTime == 0) return "—";
            TimeSpan t = TimeSpan.FromSeconds(OnlineTime);
            if (t.TotalHours >= 1)
                return string.Format("{0}ч {1}м", (int)t.TotalHours, t.Minutes);
            return string.Format("{0}м", t.Minutes);
        }
    }

    public string SignonTimeText
    {
        get { return UnixToString(SignonTime); }
    }

    public string MemberSinceText
    {
        get { return UnixToString(MemberSince); }
    }

    public string ExternalIpText
    {
        get
        {
            if (ExternalIp == 0) return "—";
            return string.Format("{0}.{1}.{2}.{3}",
                (ExternalIp >> 24) & 0xFF, (ExternalIp >> 16) & 0xFF,
                (ExternalIp >> 8) & 0xFF, ExternalIp & 0xFF);
        }
    }

    private string UnixToString(uint unix)
    {
        if (unix == 0) return "—";
        var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                     .AddSeconds(unix).ToLocalTime();
        return dt.ToString("dd.MM.yyyy HH:mm");
    }
}