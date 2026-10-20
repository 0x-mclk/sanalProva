using System.ComponentModel.DataAnnotations;
using SanalProva.Domain.Display;

namespace SanalProva.Api.Features.Displays;

public sealed record PairingStartedDto(string Code, string PairingSecret, DateTimeOffset ExpiresAt);

public enum PairingStatus
{
    Pending,
    Claimed,
    Expired,
}

public sealed record PairingStatusDto(PairingStatus Status, DateTimeOffset? ExpiresAt);

public enum TvImageKind
{
    Result,
    Photo,
}

/// <summary>TV'de gösterilecek görsel. <see cref="Url"/> kısa ömürlü imzalı bağlantıdır; saklanmamalı, günlüğe yazılmamalıdır.</summary>
public sealed record TvImageDto(Guid Id, string Url, string Label, bool IsDemo, TvImageKind Kind);

/// <summary>TV'nin tek yetkili görünümü: kendi oturumunun, sunucunun belirlediği gösterim durumu.</summary>
public sealed record TvStateDto(long Version, DisplayMode Mode, string DeviceName, bool HasSession, IReadOnlyList<TvImageDto> Images);

public sealed record DisplayDto(
    Guid Id, string Name, bool Online, DateTimeOffset? LastSeenAt, Guid? SessionId, string? SessionCode, string? SessionLabel,
    DisplayMode Mode, long StateVersion, DateTimeOffset StateChangedAt);

public sealed record ClaimDisplayRequest(
    [property: Required, StringLength(32, MinimumLength = 1)] string Code,
    [property: Required, StringLength(80, MinimumLength = 1)] string Name);

public sealed record RenameDisplayRequest([property: Required, StringLength(80, MinimumLength = 1)] string Name);

public sealed record AttachSessionRequest(Guid SessionId, bool Force = false);

/// <summary><see cref="Mode"/> yalnızca Single veya Compare olabilir; temizlemek için ayrı uç nokta vardır.</summary>
public sealed record SetDisplayStateRequest(DisplayMode Mode, [property: Required, MaxLength(2)] List<Guid> ImageIds, long? ExpectedVersion);
