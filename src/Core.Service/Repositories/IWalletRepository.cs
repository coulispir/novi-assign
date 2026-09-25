using System.Threading;
using System.Threading.Tasks;
using Core.Service.Data;
using Core.Service.Entities;
using Microsoft.EntityFrameworkCore;

namespace Core.Service.Repositories;

public interface IWalletRepository
{
    // ValueTask<T> avoids heap allocation if the Entity is already cached in DbContext memory
    ValueTask<AccountWallet?> GetByIdAsync(long id, CancellationToken cancellationToken);

    void Add(AccountWallet wallet);

    // Non-generic ValueTask for operations that return nothing but run asynchronously
    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

public class WalletRepository : IWalletRepository
{
    private readonly SystemDbContext _dbContext;

    public WalletRepository(SystemDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async ValueTask<AccountWallet?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        // FindAsync natively looks into local tracker memory first before hitting SQL Server
        return await _dbContext.AccountWallets.FindAsync(new object[] { id }, cancellationToken);
    }

    public void Add(AccountWallet wallet)
    {
        _dbContext.AccountWallets.Add(wallet);
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
