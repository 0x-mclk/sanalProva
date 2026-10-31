using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Infrastructure.Identity;

namespace SanalProva.Infrastructure.Persistence.Configurations;

/// <summary>Sunucu tarafı oturum/kilit durumu: kullanıcı silinirse satırlar da gider.</summary>
internal sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.HasIndex(x => x.UserId);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
