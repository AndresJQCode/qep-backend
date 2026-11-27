using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Messaging.Infrastructure.Persistence.Migrations;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// Every kind migration rebuilds <c>CK_message_templates_kind</c> from a literal list. The task notice migration was written on a branch that
/// did not know <c>AppointmentReminder</c> yet, so its list silently dropped it: the compiler cannot see it and the model snapshot still
/// looked right. These tests pin the latest migration list (HR birthday notice) to the enum and to the DbContext CHECK.
/// </summary>
public sealed class MessageTemplateKindCheckMigrationTests
{
    private static string AddedCheck(Migration migration, bool up) =>
        (up ? migration.UpOperations : migration.DownOperations).OfType<AddCheckConstraintOperation>()
            .Single(operation => operation.Name == "CK_message_templates_kind").Sql;

    [Fact]
    public void TheHrBirthdayNoticeMigrationCheckEqualsTheDbContextCheck()
    {
        using var context = new MessagingDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var modelSql = model.FindEntityType(typeof(MessageTemplate))!.GetCheckConstraints()
            .Single(constraint => constraint.Name == "CK_message_templates_kind").Sql;

        Assert.Equal(modelSql, AddedCheck(new AddHrBirthdayNoticeTemplateKind(), up: true));
    }

    [Fact]
    public void TheHrBirthdayNoticeMigrationCheckListsEveryKind()
    {
        var sql = AddedCheck(new AddHrBirthdayNoticeTemplateKind(), up: true);

        foreach (var name in Enum.GetNames<MessageTemplateKind>())
        {
            Assert.Contains($"'{name}'", sql);
        }
    }

    [Fact]
    public void RollingBackTheHrBirthdayNoticeMigrationRestoresTheTaskNoticeCheck()
    {
        var down = AddedCheck(new AddHrBirthdayNoticeTemplateKind(), up: false);
        var previous = AddedCheck(new AddTaskNoticeTemplateKind(), up: true);

        Assert.Equal(previous, down);
    }
}
