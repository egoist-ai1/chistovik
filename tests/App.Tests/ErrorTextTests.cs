using System.IO;
using Chistovik.App.Models;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.ViewModels;
using Chistovik.Core.Journal;
using Xunit;

namespace App.Tests;

public class ErrorTextTests
{
    [Fact]
    public void Access_errors_ask_for_administrator()
    {
        foreach (Exception e in new Exception[] { new UnauthorizedAccessException("Access is denied"), new System.Security.SecurityException("denied") })
        {
            var info = ErrorText.Describe(e);
            Assert.StartsWith("Нет доступа к данным Windows.", info.Text);
            Assert.Contains("администратора", info.Text);
            Assert.DoesNotContain("denied", info.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("denied", info.Details, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Timeouts_and_agent_silence_say_service_did_not_answer()
    {
        Assert.StartsWith("Служба Чистовика не ответила вовремя.", ErrorText.Describe(new TimeoutException("timed out")).Text);
        Assert.StartsWith("Служба Чистовика не ответила вовремя.", ErrorText.Describe(new TaskCanceledException("The request was canceled")).Text);
        Assert.StartsWith("Служба Чистовика не ответила вовремя.", ErrorText.ForClientCode(ClientCodes.Timeout).Text);
        Assert.StartsWith("Служба Чистовика не ответила вовремя.", ErrorText.ForClientCode(ClientCodes.Disconnected).Text);
    }

    [Fact]
    public void Io_errors_mention_busy_file()
    {
        var info = ErrorText.Describe(new IOException("The process cannot access the file"));
        Assert.Contains("файл занят или недоступен", info.Text);
        Assert.DoesNotContain("process", info.Text);
    }

    [Fact]
    public void Other_errors_offer_retry_and_keep_details_out_of_text()
    {
        var info = ErrorText.Describe(new InvalidOperationException("boom"));
        Assert.Equal("Не удалось получить данные. Нажмите «Повторить».", info.Text);
        Assert.Contains("boom", info.Details);
        Assert.Contains("InvalidOperationException", info.Details);
    }

    [Fact]
    public void Journal_errors_have_own_text()
    {
        Assert.Contains("повреждён", ErrorText.Describe(new JournalException(JournalException.Corrupt, "bad page")).Text);
        Assert.Contains("новой версией", ErrorText.Describe(new JournalException(JournalException.NewerSchema, "v9")).Text);
        var hot = ErrorText.Describe(new JournalException(JournalException.Recovering, "hot journal")).Text;
        Assert.Contains("восстановится при следующем запуске службы Чистовика", hot);
        Assert.DoesNotContain("Повторить", hot);
    }

    [Fact]
    public void Journal_operation_shows_title_and_time_not_id()
    {
        var id = Guid.Parse("7f3c9a2e-5b14-4d68-9e07-a1c2d3e4f5b6");
        var op = new JournalOperation(id, OperationKind.Mode, "Режим: Работа", new DateTime(2026, 10, 24, 12, 0, 0), OperationState.Done, "Завершено", true, null);
        var vm = new JournalOperationViewModel(op);
        Assert.DoesNotContain(vm.Id, vm.AutomationName);
        Assert.Contains("Режим: Работа", vm.AutomationName);
        Assert.Equal(id.ToString("D"), vm.FullId);
        Assert.Contains(vm.FullId, vm.DetailsTip);
    }
}
