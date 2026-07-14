namespace FSM.Api.Services;

public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public int HourUtc { get; set; } = 6;
}

public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } =
        "Fleet Safety Manager";
}
