using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Queue;

public record WalkInRequest(int PatientId, AppointmentType Type, bool IsUrgent, string? Notes);
public record UrgentRequest(bool IsUrgent);
/// <summary>PLAN.md 6.1: today's thread line — a short text, optionally pointed at one of today's queue entries.</summary>
public record QueueMessageRequest(string? Text, int? AppointmentId);

/// <summary>
/// Turns a queue failure into the response the UI expects, with the Arabic message shown as-is:
/// a business-rule failure (wrong status, not today, ...) is a 400, while "this clinic cannot see that row" is a
/// 404 so a cross-clinic attempt looks exactly like a missing appointment (PLAN_ADDENDUM.md 0.2).
/// </summary>
public class QueueExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case QueueItemNotFoundException notFound:
                context.Result = new NotFoundObjectResult(new { message = notFound.Message });
                context.ExceptionHandled = true;
                break;
            case QueueException ex:
                context.Result = new BadRequestObjectResult(new { message = ex.Message });
                context.ExceptionHandled = true;
                break;
        }
    }
}

[ApiController]
[Route("api/queue")]
[Authorize(Policy = Permissions.Queue)]
[TypeFilter(typeof(QueueExceptionFilter))]
public class QueueController : ControllerBase
{
    private readonly QueueService _queue;
    private readonly QueueNotifier _notifier;

    public QueueController(QueueService queue, QueueNotifier notifier)
    {
        _queue = queue;
        _notifier = notifier;
    }

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await _queue.GetSnapshotAsync(await _queue.CurrentClinicAsync()));

    [HttpPost("walk-in")]
    public async Task<IActionResult> WalkIn(WalkInRequest r)
    {
        var appt = await _queue.WalkInAsync(r.PatientId, r.Type, r.IsUrgent, r.Notes);
        await _notifier.QueueChangedAsync();
        return Ok(new { appt.Id, appt.QueueNumber, appt.TrackingToken });
    }

    [HttpPost("next")]
    public async Task<IActionResult> Next()
    {
        var next = await _queue.CallNextAsync();
        await _notifier.QueueChangedAsync();
        return Ok(next == null ? new { message = "لا يوجد مرضى في الانتظار" } : new { next.Id, next.QueueNumber, next.PatientName, next.PatientId } as object);
    }

    // PLAN.md 6.1: the day's front-desk ↔ clinical team thread, read back through the ordinary authorized door.
    [HttpGet("messages")]
    public async Task<IActionResult> Messages() => Ok(await _queue.TodayMessagesAsync());

    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage(QueueMessageRequest r)
    {
        var message = await _queue.SendMessageAsync(r.Text, r.AppointmentId);
        await _notifier.MessagesChangedAsync();
        return Ok(message);
    }

    [HttpPost("{id:int}/check-in")]
    public Task<IActionResult> CheckIn(int id) => Act(() => _queue.CheckInAsync(id));

    [HttpPost("{id:int}/start")]
    public Task<IActionResult> Start(int id) => Act(() => _queue.StartAsync(id));

    [HttpPost("{id:int}/finish")]
    public Task<IActionResult> Finish(int id) => Act(() => _queue.FinishAsync(id));

    [HttpPost("{id:int}/postpone")]
    public Task<IActionResult> Postpone(int id) => Act(() => _queue.PostponeAsync(id));

    [HttpPost("{id:int}/no-show")]
    public Task<IActionResult> NoShow(int id) => Act(() => _queue.MarkNoShowAsync(id));

    [HttpPost("{id:int}/urgent")]
    public Task<IActionResult> Urgent(int id, UrgentRequest r) => Act(() => _queue.SetUrgentAsync(id, r.IsUrgent));

    private async Task<IActionResult> Act(Func<Task> action)
    {
        await action();
        await _notifier.QueueChangedAsync();
        return NoContent();
    }
}
