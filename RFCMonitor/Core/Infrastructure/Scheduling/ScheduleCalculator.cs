using Cronos;
using MonitorRFC.Contracts;
namespace MonitorRFC.Infrastructure.Scheduling;

public static class ScheduleCalculator
{
    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) when (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        {
            TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows);
            return TimeZoneInfo.FindSystemTimeZoneById(windows!);
        }
    }
    public static void Validate(ScheduleOptions schedule)
    {
        _ = Zone(schedule.TimeZone);
        if (schedule.IntervalMinutes < 1) throw new ArgumentException("El intervalo debe ser positivo.");
        if (schedule.Mode is not ("interval" or "daily" or "weekly" or "cron" or "daily-hours"))
            throw new ArgumentException("Programación desconocida.");
        if (!TimeOnly.TryParseExact(schedule.LocalTime, "HH:mm", out _)) throw new ArgumentException("Hora inválida; usar HH:mm.");
        if (schedule.WeekDays.Length == 0 || schedule.WeekDays.Any(d => d is < 0 or > 6))
            throw new ArgumentException("Días de semana inválidos (0=domingo).");
        try { _ = CronExpression.Parse(Expression(schedule)); }
        catch (CronFormatException) { throw new ArgumentException("Expresión cron inválida (cinco campos)."); }
    }
    private static string Expression(ScheduleOptions schedule)
    {
        var time = TimeOnly.ParseExact(schedule.LocalTime, "HH:mm");
        return schedule.Mode switch
        {
            "daily" => $"{time.Minute} {time.Hour} * * *",
            "weekly" => $"{time.Minute} {time.Hour} * * {string.Join(',', schedule.WeekDays.Distinct().Order())}",
            _ => schedule.Cron
        };
    }
    public static DateTimeOffset? Next(ScheduleOptions schedule, DateTimeOffset after)
    {
        Validate(schedule);
        if (!schedule.Enabled) return null;
        return schedule.Mode == "interval" ? after.AddMinutes(schedule.IntervalMinutes) :
            CronExpression.Parse(Expression(schedule)).GetNextOccurrence(after, Zone(schedule.TimeZone));
    }
}
