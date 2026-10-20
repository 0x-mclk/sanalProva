using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SanalProva.Domain.Common;
using SanalProva.Infrastructure.Identity;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>created_at / updated_at alanlarını tek yerden, test edilebilir saatle (<see cref="TimeProvider"/>) doldurur.</summary>
internal sealed class TimestampInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case Entity entity:
                    if (entry.State == EntityState.Added)
                    {
                        entity.CreatedAt = now;
                    }

                    entity.UpdatedAt = now;
                    break;
                case AppUser user:
                    if (entry.State == EntityState.Added)
                    {
                        user.CreatedAt = now;
                    }

                    user.UpdatedAt = now;
                    break;
            }
        }
    }
}
