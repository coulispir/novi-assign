using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Repositories;

public interface IWalletRepository
{
    // Tracked, for commands that change the wallet and save it
    ValueTask<AccountWallet?> GetByIdAsync(long id, CancellationToken cancellationToken);

    // For reads that never save: the wallet isn't tracked, so it can't be modified by mistake
    ValueTask<AccountWallet?> GetByIdReadOnlyAsync(long id, CancellationToken cancellationToken);

    void Add(AccountWallet wallet);

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
        // FindAsync returns the tracked instance if this context already loaded the wallet
        return await _dbContext.AccountWallets.FindAsync(new object[] { id }, cancellationToken);
    }

    public async ValueTask<AccountWallet?> GetByIdReadOnlyAsync(long id, CancellationToken cancellationToken)
    {
        return await _dbContext.AccountWallets.AsNoTracking().FirstOrDefaultAsync(wallet => wallet.Id == id, cancellationToken);
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
