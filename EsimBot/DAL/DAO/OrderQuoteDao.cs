namespace EsimBot.DAL.DAO;

public sealed record OrderQuoteDao(string Id, long UserId, string RequestKey, string PackageCode,
    string PackageName, long CostUnits, int Stars, string TermsVersion, DateTime ExpiresAt);
