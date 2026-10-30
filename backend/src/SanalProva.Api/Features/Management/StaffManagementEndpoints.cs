using System.ComponentModel.DataAnnotations;
using SanalProva.Api.Features.Platform;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;

namespace SanalProva.Api.Features.Management;

public sealed record StaffAssignmentRequest([property: Required] Guid StoreId, [property: Required] StoreRole Role);

/// <param name="Assignments">Şube × rol listesi. Owner rolü verilirse firmadaki tüm şubelerde Owner olur.</param>
public sealed record InviteStaffRequest(
    [property: Required, StringLength(254), EmailAddress] string Email,
    [property: Required, StringLength(80, MinimumLength = 2)] string DisplayName,
    [property: Required, MinLength(1), MaxLength(50)] List<StaffAssignmentRequest> Assignments);

public sealed record SetStaffAssignmentsRequest([property: Required, MaxLength(50)] List<StaffAssignmentRequest> Assignments);

public sealed record SetStaffStatusRequest(bool IsActive);

/// <param name="SetupLink">Göreli yol + fragment; yalnızca bu yanıtta görünür, bir daha gösterilmez (kaybolursa yeniden üretilir).</param>
public sealed record InviteStaffResponse(StaffMemberDto Member, string SetupLink, DateTimeOffset SetupLinkExpiresAt);

/// <summary>
/// Personel yönetimi (M5.2b/c): Owner ve Manager. Yetki sınırı <see cref="StaffManagementService"/>'te her işlemde güncel rollerden hesaplanır:
/// Owner firmadaki herkesi yönetir; Manager yalnızca kendi şubelerindeki Staff'ı. Başka firmanın kullanıcısı 404 döner.
/// Bağlantı içeren yanıtlar önbelleğe alınmaz.
/// </summary>
public static class StaffManagementEndpoints
{
    public static RouteGroupBuilder MapStaffManagementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/management/users").WithTags("Yönetim").RequireOrganizationRole(StoreRole.Manager);
        group.MapGet("", ListAsync).Produces<IReadOnlyList<StaffMemberDto>>();
        group.MapPost("", InviteAsync).Produces<InviteStaffResponse>(StatusCodes.Status201Created);
        group.MapPut("/{id:guid}/assignments", SetAssignmentsAsync).Produces<StaffMemberDto>();
        group.MapPatch("/{id:guid}/status", SetStatusAsync).Produces<StaffMemberDto>();
        group.MapPost("/{id:guid}/setup-link", IssueLinkAsync).Produces<StaffSetupLinkDto>();
        group.MapDelete("/{id:guid}/setup-link", RevokeLinksAsync).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static Task<IResult> ListAsync(StaffManagementService service, CurrentUser user, CancellationToken ct) =>
        Run(async () => TypedResults.Ok(await service.ListAsync(user.UserId, user.OrganizationId, ct)));

    private static Task<IResult> InviteAsync(InviteStaffRequest request, StaffManagementService service, CurrentUser user, HttpContext http, CancellationToken ct) =>
        Run(async () =>
        {
            PlatformOrganizationEndpoints.NoStore(http);
            var result = await service.InviteAsync(
                user.UserId, user.OrganizationId, request.Email.Trim(), request.DisplayName.Trim(), request.Assignments.Select(a => new StaffAssignment(a.StoreId, a.Role)).ToList(), ct);
            return TypedResults.Created($"/api/v1/management/users/{result.Member.Id}", new InviteStaffResponse(result.Member, result.SetupLink, result.ExpiresAt));
        });

    private static Task<IResult> SetAssignmentsAsync(Guid id, SetStaffAssignmentsRequest request, StaffManagementService service, CurrentUser user, CancellationToken ct) =>
        Run(async () => TypedResults.Ok(await service.SetAssignmentsAsync(user.UserId, user.OrganizationId, id, request.Assignments.Select(a => new StaffAssignment(a.StoreId, a.Role)).ToList(), ct)));

    private static Task<IResult> SetStatusAsync(Guid id, SetStaffStatusRequest request, StaffManagementService service, CurrentUser user, CancellationToken ct) =>
        Run(async () => TypedResults.Ok(await service.SetActiveAsync(user.UserId, user.OrganizationId, id, request.IsActive, ct)));

    private static Task<IResult> IssueLinkAsync(Guid id, StaffManagementService service, CurrentUser user, HttpContext http, CancellationToken ct) =>
        Run(async () =>
        {
            PlatformOrganizationEndpoints.NoStore(http);
            return TypedResults.Ok(await service.IssueLinkAsync(user.UserId, user.OrganizationId, id, ct));
        });

    private static Task<IResult> RevokeLinksAsync(Guid id, StaffManagementService service, CurrentUser user, CancellationToken ct) =>
        Run(async () =>
        {
            await service.RevokeLinksAsync(user.UserId, user.OrganizationId, id, ct);
            return TypedResults.NoContent();
        });

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StaffException ex)
        {
            return ex.Failure switch
            {
                StaffFailure.NotFound => ApiProblems.NotFound("Kullanıcı bulunamadı."),
                StaffFailure.Forbidden => ApiProblems.Forbidden("INSUFFICIENT_ROLE", "Bu kişi üzerinde işlem yapma yetkiniz yok."),
                StaffFailure.ManagerLimit => ApiProblems.Forbidden("MANAGER_LIMIT", "Yönetici yalnızca kendi şubelerine Staff ekleyip çıkarabilir."),
                StaffFailure.SelfChange => ApiProblems.Forbidden("SELF_CHANGE_FORBIDDEN", "Kendi rolünüzü veya durumunuzu değiştiremezsiniz."),
                StaffFailure.EmailTaken => ApiProblems.Conflict("EMAIL_TAKEN", "Bu e-posta adresiyle zaten bir hesap var."),
                StaffFailure.StoreNotFound => ApiProblems.BadRequest("STORE_NOT_FOUND", "Şube bulunamadı."),
                StaffFailure.StoreInactive => ApiProblems.Conflict("STORE_INACTIVE", "Pasif şubeye yeni atama yapılamaz."),
                StaffFailure.NoAssignment => ApiProblems.BadRequest("ASSIGNMENT_REQUIRED", "En az bir şube ataması gerekli."),
                StaffFailure.LastOwner => ApiProblems.Conflict("LAST_OWNER", "Firmadaki son etkin Owner'ın yetkisi kaldırılamaz veya hesabı pasife alınamaz."),
                StaffFailure.UserInactive => ApiProblems.Conflict("USER_INACTIVE", "Pasif hesap için bağlantı üretilemez; önce hesabı etkinleştirin."),
                _ => ApiProblems.Create(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "Beklenmeyen bir hata oluştu."),
            };
        }
    }
}
