using backend.Dtos;
using backend.Repositories;

namespace backend.Services;

public class ClarificationService
{
    private readonly ISqlRepository _db;
    private readonly ILogger<ClarificationService> _logger;

    public ClarificationService(ISqlRepository db, ILogger<ClarificationService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<ClarificationDto?> DetectAsync(
        string userQuestion,
        QueryEntitiesDto entities,
        int userId,
        CancellationToken ct)
    {
        try
        {
            // CHECK 1 — Bank
            if (!string.IsNullOrWhiteSpace(entities.BankName))
            {
                const string bankSql = """
                    SELECT DISTINCT lrd.bank
                    FROM lc_request_details lrd
                    JOIN user_business_unit_mapping ubm
                        ON ubm.business_unit_id = lrd.business_unit_id
                    WHERE lrd.is_active = 1 AND ubm.user_id = @UserId
                    ORDER BY lrd.bank
                    """;

                var banks = (await _db.QueryAsync<string>(bankSql, new { UserId = userId }, ct)).ToList();
                var found = banks.Any(b => b != null &&
                    b.Contains(entities.BankName, StringComparison.OrdinalIgnoreCase));

                if (!found)
                {
                    return new ClarificationDto
                    {
                        EntityType        = "bank",
                        UnrecognisedValue = entities.BankName,
                        AvailableOptions  = banks.Where(b => b != null).ToArray()!,
                        QuestionTemplate  = userQuestion
                    };
                }
            }

            // CHECK 2 — Customer
            if (!string.IsNullOrWhiteSpace(entities.CustomerName))
            {
                const string customerSql = """
                    SELECT DISTINCT TOP(20) c.name
                    FROM customers c
                    JOIN lc_request_details lrd ON lrd.customer_id = c.obj_id
                    JOIN user_business_unit_mapping ubm
                        ON ubm.business_unit_id = lrd.business_unit_id
                    WHERE lrd.is_active = 1 AND ubm.user_id = @UserId
                    ORDER BY c.name
                    """;

                var customers = (await _db.QueryAsync<string>(customerSql, new { UserId = userId }, ct)).ToList();
                var found = customers.Any(c => c != null &&
                    c.Contains(entities.CustomerName, StringComparison.OrdinalIgnoreCase));

                if (!found)
                {
                    return new ClarificationDto
                    {
                        EntityType        = "customer",
                        UnrecognisedValue = entities.CustomerName,
                        AvailableOptions  = customers.Where(c => c != null).ToArray()!,
                        QuestionTemplate  = userQuestion
                    };
                }
            }

            // CHECK 3 — LC Number
            if (!string.IsNullOrWhiteSpace(entities.LcNumber))
            {
                const string lcSql = """
                    SELECT TOP(10) ld.lc_number
                    FROM lc_details ld
                    JOIN lc_request_details lrd ON lrd.obj_id = ld.lc_request_id
                    JOIN user_business_unit_mapping ubm
                        ON ubm.business_unit_id = lrd.business_unit_id
                    WHERE lrd.is_active = 1 AND ubm.user_id = @UserId
                        AND ld.lc_number IS NOT NULL
                    ORDER BY lrd.created_on DESC
                    """;

                var lcNumbers = (await _db.QueryAsync<string>(lcSql, new { UserId = userId }, ct)).ToList();
                var found = lcNumbers.Any(n => n != null &&
                    n.Contains(entities.LcNumber, StringComparison.OrdinalIgnoreCase));

                if (!found)
                {
                    return new ClarificationDto
                    {
                        EntityType        = "lc_number",
                        UnrecognisedValue = entities.LcNumber,
                        AvailableOptions  = lcNumbers.Where(n => n != null).ToArray()!,
                        QuestionTemplate  = userQuestion
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClarificationService.DetectAsync failed — returning null");
            return null;
        }
    }
}
