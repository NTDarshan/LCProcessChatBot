using backend.Models;

namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  IntentRouterService  –  maps every intent to a correct, parameterised SQL.
//
//  FIXES IN THIS VERSION:
//
//  1.  TopBanks — REMOVED "WHERE s.application_status = 'LCIssued'" filter.
//      Original query only counted LCs that reached issued state → banks with
//      only pending/draft LCs returned 0 rows ("No data found").
//      Now counts ALL LCs per bank with full per-status breakdown columns.
//      Added ISNULL(...,0) guards on all aggregates.
//      Added DraftCount, ValidationCount, RejectedCount for richer chart data.
//
//  2.  ExpiredLC — dual condition: lC_expired flag OR lc_expire_date < GETDATE().
//      In real data lC_expired = 0 on most rows (flag is set only when manually
//      amended). Date comparison is the reliable fallback.
//      Removed status filter — an LC can be expired regardless of request status.
//
//  3.  ExpiringLC — added lc_expire_date IS NOT NULL guard and changed to
//      lc_expire_date >= GETDATE() so we never return already-expired LCs in
//      the "expiring soon" list.
//
//  4.  StatusBreakdown — added HumanLabel CASE column so frontend shows
//      "Pending N+1" not "Submitted_N+1". Added TotalAmount with COALESCE
//      so draft/pending requests still contribute their requested amount.
//
//  5.  CustomerLC — added RejectedCount, CancelledCount, ValidationCount,
//      DraftCount for full stacked bar chart. Added TotalRequestedAmount as
//      fallback when LC not yet issued. Added ISNULL guards on all aggregates.
//
//  6.  AmendmentRequests — AmendedLcNumber alias renamed to LcNumber so the
//      frontend INTENT_COLUMN_MAP key 'LcNumber' resolves correctly.
//
//  7.  BaseQuery — COALESCE(ld.amount, lrd.total_amount) AS LcAmount so that
//      requests without an issued LC still show a meaningful amount.
//      ISNULL(ld.currency, lrd.currency) AS Currency for same reason.
//      Added StatusLabel CASE column (human-readable) to every base query row.
//      Added DaysPending for urgency calculations.
//
//  8.  OutstandingLC / MyApprovals — column aliases aligned with frontend
//      INTENT_COLUMN_MAP: LcIssueDate, LcExpiryDate, BeneficiaryOnLC.
//
//  9.  AmendmentCount — GROUP BY expanded to include all selected non-aggregated
//      columns to avoid SQL Server "not in aggregate or GROUP BY" errors.
//
//  10. LcHistory — added Bank, Product, Status columns and ISNULL fallback for
//      LcNumber so the timeline has full context per event.
// ─────────────────────────────────────────────────────────────────────────────
public class IntentRouterService
{
    private readonly Dictionary<string, IntentDefinition> _intents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── 1. APPROVAL WORKFLOW ─────────────────────────────────────────────

            ["PendingApprovals"] = new IntentDefinition
            {
                Name = "PendingApprovals",
                Sql = BaseQuery("""
                AND s.application_status IN ('Submitted_N+1','Submitted_N+2')
                ORDER BY lrd.created_on DESC
                """)
            },

            ["PendingApprovalsN1"] = new IntentDefinition
            {
                Name = "PendingApprovalsN1",
                Sql = BaseQuery("""
                AND s.application_status = 'Submitted_N+1'
                ORDER BY lrd.created_on DESC
                """)
            },

            ["PendingApprovalsN2"] = new IntentDefinition
            {
                Name = "PendingApprovalsN2",
                Sql = BaseQuery("""
                AND s.application_status = 'Submitted_N+2'
                ORDER BY lrd.created_on DESC
                """)
            },

            ["ApprovalBottleneck"] = new IntentDefinition
            {
                Name = "ApprovalBottleneck",
                Sql = BaseQuery("""
                AND s.application_status IN ('Submitted_N+1','Submitted_N+2','Submitted_For_Validation')
                AND DATEDIFF(DAY, lrd.created_on, GETDATE()) > ISNULL(@DaysRange, 3)
                ORDER BY lrd.created_on ASC
                """)
            },

            // My approvals: LCs where the logged-in user is a named approver
            ["MyApprovals"] = new IntentDefinition
            {
                Name = "MyApprovals",
                Sql = $@"
                SELECT
                    lrd.obj_id                                  AS RequestId,
                    lrd.bank                                    AS Bank,
                    lrd.product                                 AS Product,
                    lrd.total_amount                            AS RequestedAmount,
                    lrd.type_of_lc                              AS TypeOfLcRequested,
                    lrd.suppliername                            AS SupplierName,
                    c.name                                      AS CustomerName,
                    c.sap_sold_to_name                          AS SapCustomerName,
                    bu.business_unit_name                       AS BusinessUnit,
                    ld.lc_number                                AS LcNumber,
                    COALESCE(ld.amount, lrd.total_amount)       AS LcAmount,
                    ISNULL(ld.currency, lrd.currency)           AS Currency,
                    ld.lc_issue_date                            AS LcIssueDate,
                    ld.lc_expire_date                           AS LcExpiryDate,
                    ld.grace_period                             AS GracePeriod,
                    ld.beneficiary_name_on_LC                   AS BeneficiaryOnLC,
                    ld.payment_terms                            AS PaymentTerms,
                    ld.type_of_LC                               AS TypeOfLC,
                    ld.shipment_date                            AS ActualShipmentDate,
                    s.application_status                        AS Status,
                    CASE s.application_status
                        WHEN 'Draft'                        THEN 'Draft'
                        WHEN 'Submitted_For_Validation'     THEN 'In Validation'
                        WHEN 'Submitted_N+1'                THEN 'Pending N+1'
                        WHEN 'Submitted_N+2'                THEN 'Pending N+2'
                        WHEN 'LCIssued'                     THEN 'Issued'
                        WHEN 'PaymentDone'                  THEN 'Paid'
                        WHEN 'PaymentNotDone'               THEN 'Unpaid'
                        WHEN 'Rejected'                     THEN 'Rejected'
                        WHEN 'Cancelled'                    THEN 'Cancelled'
                        ELSE s.application_status
                    END                                         AS StatusLabel,
                    lam.status                                  AS ApprovalStatus,
                    lam.is_approved_offline                     AS IsOfflineApproval,
                    lam.assigned_on                             AS AssignedOn,
                    lam.action_taken_on                         AS ActionTakenOn,
                    DATEDIFF(DAY, lrd.created_on, GETDATE())    AS DaysPending,
                    lrd.created_on                              AS RequestCreatedOn
                FROM lc_approver_mapping lam
                JOIN lc_request_details lrd ON lrd.obj_id       = lam.lc_request_id
                JOIN statuses           s   ON s.obj_id         = lrd.status_id
                JOIN customers          c   ON c.obj_id         = lrd.customer_id
                JOIN business_unit      bu  ON bu.obj_id        = lrd.business_unit_id
                LEFT JOIN lc_details    ld  ON ld.lc_request_id = lrd.obj_id
                WHERE lam.approver_id = @UserId
                  AND lrd.is_active   = 1
                  AND (@BankName     IS NULL OR UPPER(lrd.bank)  LIKE '%' + UPPER(@BankName)     + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)    LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@MinAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) >= @MinAmount)
                  AND (@MaxAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) <= @MaxAmount)
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                ORDER BY lam.assigned_on DESC"
            },

            // ── 2. LC STATUS & LIFECYCLE ─────────────────────────────────────────

            ["DraftLC"] = new IntentDefinition
            {
                Name = "DraftLC",
                Sql = BaseQuery("""
                AND s.application_status = 'Draft'
                ORDER BY lrd.created_on DESC
                """)
            },

            ["IssuedLC"] = new IntentDefinition
            {
                Name = "IssuedLC",
                Sql = BaseQuery("""
                AND s.application_status = 'LCIssued'
                ORDER BY ld.lc_issue_date DESC
                """)
            },

            ["PaidLC"] = new IntentDefinition
            {
                Name = "PaidLC",
                Sql = BaseQuery("""
                AND s.application_status = 'PaymentDone'
                ORDER BY ld.ami_payment_date DESC
                """)
            },

            ["RejectedLC"] = new IntentDefinition
            {
                Name = "RejectedLC",
                Sql = BaseQuery("""
                AND s.application_status = 'Rejected'
                ORDER BY lrd.created_on DESC
                """)
            },

            ["CancelledLC"] = new IntentDefinition
            {
                Name = "CancelledLC",
                Sql = BaseQuery("""
                AND s.application_status = 'Cancelled'
                ORDER BY lrd.created_on DESC
                """)
            },

            ["ValidationPending"] = new IntentDefinition
            {
                Name = "ValidationPending",
                Sql = BaseQuery("""
                AND s.application_status LIKE 'Submitted_For_Validation%'
                ORDER BY lrd.created_on DESC
                """)
            },

            // ── 3. EXPIRY / TIME BASED ───────────────────────────────────────────

            // FIX: explicit IS NOT NULL + >= GETDATE() guard so "expiring" never returns expired LCs
            ["ExpiringLC"] = new IntentDefinition
            {
                Name = "ExpiringLC",
                Sql = BaseQuery("""
                AND s.application_status = 'LCIssued'
                AND ld.lc_expire_date IS NOT NULL
                AND ld.lc_expire_date >= GETDATE()
                AND ld.lc_expire_date <= DATEADD(DAY, ISNULL(@DaysRange, 30), GETDATE())
                ORDER BY ld.lc_expire_date ASC
                """)
            },

            // FIX: flag OR date comparison — flag is stale in many real rows
            ["ExpiredLC"] = new IntentDefinition
            {
                Name = "ExpiredLC",
                Sql = BaseQuery("""
                AND (
                    ld.lC_expired = 1
                    OR (ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date < GETDATE())
                )
                ORDER BY ld.lc_expire_date DESC
                """)
            },

            // ── 4. OUTSTANDING / PAYMENT ─────────────────────────────────────────

            // FIX: column aliases aligned with frontend INTENT_COLUMN_MAP
            ["OutstandingLC"] = new IntentDefinition
            {
                Name = "OutstandingLC",
                Sql = $@"
                SELECT
                    lrd.obj_id                                  AS RequestId,
                    lrd.bank                                    AS Bank,
                    lrd.product                                 AS Product,
                    c.name                                      AS CustomerName,
                    c.sap_sold_to_name                          AS SapCustomerName,
                    bu.business_unit_name                       AS BusinessUnit,
                    ld.lc_number                                AS LcNumber,
                    COALESCE(ld.amount, lrd.total_amount)       AS LcAmount,
                    ISNULL(ld.currency, lrd.currency)           AS Currency,
                    ld.lc_issue_date                            AS LcIssueDate,
                    ld.lc_expire_date                           AS LcExpiryDate,
                    ld.grace_period                             AS GracePeriod,
                    ld.beneficiary_name_on_LC                   AS BeneficiaryOnLC,
                    ld.payment_terms                            AS PaymentTerms,
                    ld.type_of_LC                               AS TypeOfLC,
                    ld.port_Of_loading                          AS PortOfLoading,
                    ld.port_Of_discharge                        AS PortOfDischarge,
                    s.application_status                        AS Status,
                    CASE s.application_status
                        WHEN 'LCIssued'       THEN 'Issued'
                        WHEN 'PaymentNotDone' THEN 'Unpaid'
                        ELSE s.application_status
                    END                                         AS StatusLabel,
                    inv.obj_id                                  AS InvoiceId,
                    inv.invoice_amount                          AS InvoiceAmount,
                    inv.invoice_Date                            AS InvoiceDate,
                    inv.is_Mark_as_paid                         AS IsPaid,
                    inv.ami_pmt_date                            AS ExpectedPaymentDate,
                    inv.beneficiary_pmt_date                    AS BeneficiaryPaymentDate,
                    inv.lc_invoice_amount_usd                   AS InvoiceAmountUsd,
                    inv.usd_bank_charges                        AS BankCharges,
                    DATEDIFF(DAY, ld.lc_expire_date, GETDATE()) AS DaysOverdue,
                    DATEDIFF(DAY, lrd.created_on, GETDATE())    AS DaysPending,
                    lrd.created_on                              AS RequestCreatedOn
                FROM lc_request_details lrd
                JOIN statuses           s   ON s.obj_id          = lrd.status_id
                JOIN customers          c   ON c.obj_id          = lrd.customer_id
                JOIN business_unit      bu  ON bu.obj_id         = lrd.business_unit_id
                LEFT JOIN lc_details    ld  ON ld.lc_request_id  = lrd.obj_id
                LEFT JOIN invoice_details inv ON inv.lc_request_id = lrd.obj_id
                         AND inv.lc_details_id  = ld.obj_id
                         AND inv.is_Mark_as_paid = 0
                WHERE lrd.is_active = 1
                  AND s.application_status IN ('LCIssued','PaymentNotDone')
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@LcNumber     IS NULL OR UPPER(ld.lc_number)    = UPPER(@LcNumber))
                  AND (@MinAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) >= @MinAmount)
                  AND (@MaxAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) <= @MaxAmount)
                  AND (@BankName     IS NULL OR UPPER(lrd.bank)        LIKE '%' + UPPER(@BankName)     + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)          LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                  AND (@Country      IS NULL OR UPPER(ld.port_Of_loading)  LIKE '%' + UPPER(@Country) + '%'
                                            OR UPPER(ld.port_Of_discharge) LIKE '%' + UPPER(@Country) + '%')
                ORDER BY ld.lc_expire_date ASC"
            },

            // ── 5. AMENDMENTS ────────────────────────────────────────────────────

            // FIX: AmendedLcNumber → LcNumber to match frontend INTENT_COLUMN_MAP key
            ["AmendmentRequests"] = new IntentDefinition
            {
                Name = "AmendmentRequests",
                Sql = $@"
                SELECT
                    lad.obj_id                              AS AmendmentId,
                    lrd.obj_id                              AS RequestId,
                    lrd.bank                                AS Bank,
                    lrd.product                             AS Product,
                    c.name                                  AS CustomerName,
                    lad.lc_number                           AS LcNumber,
                    lad.amount                              AS AmendedAmount,
                    lad.currency                            AS Currency,
                    lad.lc_expire_date                      AS NewExpiryDate,
                    lad.grace_period                        AS NewGracePeriod,
                    lad.payment_terms                       AS NewPaymentTerms,
                    lad.type_of_LC                          AS TypeOfLC,
                    lad.amendment_details                   AS WhatChanged,
                    lad.file_name                           AS DocumentFileName,
                    u.first_name + ' ' + u.last_name        AS AmendedBy,
                    lad.created_on                          AS AmendedOn
                FROM lc_amendment_details lad
                JOIN lc_request_details lrd ON lrd.obj_id  = lad.lc_request_id
                JOIN customers          c   ON c.obj_id    = lrd.customer_id
                JOIN users              u   ON u.obj_id    = lad.created_by
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@LcNumber     IS NULL OR UPPER(lad.lc_number)  = UPPER(@LcNumber))
                  AND (@BankName     IS NULL OR UPPER(lrd.bank)       LIKE '%' + UPPER(@BankName)     + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)         LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@MinAmount    IS NULL OR lad.amount             >= @MinAmount)
                  AND (@MaxAmount    IS NULL OR lad.amount             <= @MaxAmount)
                  AND (@CurrencyCode IS NULL OR UPPER(lad.currency)   = UPPER(@CurrencyCode))
                  AND (@DaysRange    IS NULL OR lad.created_on        >= DATEADD(DAY, -@DaysRange, GETDATE()))
                ORDER BY lad.created_on DESC"
            },

            // FIX: GROUP BY expanded to all non-aggregated columns (SQL Server strict mode)
            ["AmendmentCount"] = new IntentDefinition
            {
                Name = "AmendmentCount",
                Sql = $@"
                SELECT
                    lrd.obj_id                                      AS RequestId,
                    lrd.bank                                        AS Bank,
                    c.name                                          AS CustomerName,
                    ISNULL(ld.lc_number, lrd.contract_number)       AS LcNumber,
                    ISNULL(ld.currency, lrd.currency)               AS Currency,
                    COALESCE(ld.amount, lrd.total_amount)           AS LcAmount,
                    COUNT(lad.obj_id)                               AS AmendmentCount,
                    MAX(lad.created_on)                             AS LastAmendedOn,
                    s.application_status                            AS Status,
                    CASE s.application_status
                        WHEN 'LCIssued'   THEN 'Issued'
                        WHEN 'PaymentDone' THEN 'Paid'
                        ELSE s.application_status
                    END                                             AS StatusLabel
                FROM lc_request_details lrd
                JOIN statuses              s   ON s.obj_id         = lrd.status_id
                JOIN customers             c   ON c.obj_id         = lrd.customer_id
                LEFT JOIN lc_details       ld  ON ld.lc_request_id = lrd.obj_id
                LEFT JOIN lc_amendment_details lad ON lad.lc_request_id = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@BankName     IS NULL OR UPPER(lrd.bank) LIKE '%' + UPPER(@BankName) + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)   LIKE '%' + UPPER(@CustomerName) + '%')
                GROUP BY lrd.obj_id, lrd.bank, c.name,
                         ld.lc_number, lrd.contract_number,
                         ld.currency, lrd.currency,
                         ld.amount, lrd.total_amount,
                         s.application_status
                HAVING COUNT(lad.obj_id) > 0
                ORDER BY AmendmentCount DESC"
            },

            // ── 6. INVOICE / PAYMENT ─────────────────────────────────────────────

            ["InvoiceStatus"] = new IntentDefinition
            {
                Name = "InvoiceStatus",
                Sql = $@"
                SELECT
                    lrd.obj_id                              AS RequestId,
                    lrd.bank                                AS Bank,
                    c.name                                  AS CustomerName,
                    ld.lc_number                            AS LcNumber,
                    inv.obj_id                              AS InvoiceId,
                    inv.invoice_amount                      AS InvoiceAmount,
                    inv.currency                            AS Currency,
                    inv.invoice_Date                        AS InvoiceDate,
                    inv.shipment_date                       AS ShipmentDate,
                    inv.beneficiary                         AS Beneficiary,
                    inv.document_set_number                 AS DocumentSetNumber,
                    inv.is_Mark_as_paid                     AS IsPaid,
                    inv.is_Marked_as_final_update           AS IsFinalUpdate,
                    inv.ami_pmt_date                        AS ExpectedPaymentDate,
                    inv.beneficiary_pmt_date                AS BeneficiaryPaymentDate,
                    inv.lc_invoice_amount_usd               AS InvoiceAmountUsd,
                    inv.usd_bank_charges                    AS BankCharges,
                    inv.is_Refunded                         AS IsRefunded,
                    inv.Refund_Value_Date                   AS RefundValueDate,
                    s.application_status                    AS RequestStatus
                FROM invoice_details inv
                JOIN lc_request_details lrd ON lrd.obj_id  = inv.lc_request_id
                JOIN statuses           s   ON s.obj_id    = lrd.status_id
                JOIN customers          c   ON c.obj_id    = lrd.customer_id
                LEFT JOIN lc_details    ld  ON ld.obj_id   = inv.lc_details_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@LcNumber     IS NULL OR UPPER(ld.lc_number)  = UPPER(@LcNumber))
                  AND (@BankName     IS NULL OR UPPER(lrd.bank)      LIKE '%' + UPPER(@BankName)     + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)        LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@MinAmount    IS NULL OR inv.invoice_amount    >= @MinAmount)
                  AND (@MaxAmount    IS NULL OR inv.invoice_amount    <= @MaxAmount)
                  AND (@CurrencyCode IS NULL OR UPPER(inv.currency)  = UPPER(@CurrencyCode))
                ORDER BY inv.invoice_Date DESC"
            },

            // ── 7. DELAYS / SLA ──────────────────────────────────────────────────

            ["DelayedRequests"] = new IntentDefinition
            {
                Name = "DelayedRequests",
                Sql = BaseQuery("""
                AND s.application_status NOT IN ('LCIssued','PaymentDone','Rejected','Cancelled')
                AND DATEDIFF(DAY, lrd.created_on, GETDATE()) > ISNULL(@DaysRange, 3)
                ORDER BY lrd.created_on ASC
                """)
            },

            // ── 8. AGGREGATIONS / INSIGHTS ───────────────────────────────────────

            // FIX: removed status filter — was root cause of "No data found".
            // Now counts ALL LCs per bank. ISNULL guards on all aggregates.
            ["TopBanks"] = new IntentDefinition
            {
                Name = "TopBanks",
                Sql = $@"
                SELECT
                    lrd.bank                                                            AS Bank,
                    COUNT(DISTINCT lrd.obj_id)                                          AS LcCount,
                    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0)               AS TotalLcValue,
                    ISNULL(SUM(CASE WHEN s.application_status = 'LCIssued'
                                    THEN 1 ELSE 0 END), 0)                             AS IssuedCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'PaymentDone'
                                    THEN 1 ELSE 0 END), 0)                             AS PaidCount,
                    ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2')
                                    THEN 1 ELSE 0 END), 0)                             AS PendingCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'Draft'
                                    THEN 1 ELSE 0 END), 0)                             AS DraftCount,
                    ISNULL(SUM(CASE WHEN s.application_status LIKE 'Submitted_For_Validation%'
                                    THEN 1 ELSE 0 END), 0)                             AS ValidationCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'Rejected'
                                    THEN 1 ELSE 0 END), 0)                             AS RejectedCount,
                    ISNULL(SUM(CASE WHEN s.application_status IN ('LCIssued','PaymentDone','PaymentNotDone')
                                    THEN COALESCE(ld.amount, lrd.total_amount) ELSE 0 END), 0) AS ActiveLcValue
                FROM lc_request_details lrd
                JOIN statuses        s   ON s.obj_id          = lrd.status_id
                LEFT JOIN lc_details ld  ON ld.lc_request_id  = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@BankName     IS NULL OR UPPER(lrd.bank) LIKE '%' + UPPER(@BankName) + '%')
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                GROUP BY lrd.bank
                HAVING COUNT(DISTINCT lrd.obj_id) > 0
                ORDER BY TotalLcValue DESC"
            },

            // FIX: HumanLabel CASE column added. TotalAmount uses COALESCE so pending
            // requests still contribute their requested amount to the total.
            ["StatusBreakdown"] = new IntentDefinition
            {
                Name = "StatusBreakdown",
                Sql = $@"
                SELECT
                    s.application_status                            AS Status,
                    CASE s.application_status
                        WHEN 'Draft'                            THEN 'Draft'
                        WHEN 'Submitted_For_Validation'         THEN 'In Validation'
                        WHEN 'Submitted_N+1'                    THEN 'Pending N+1'
                        WHEN 'Submitted_N+2'                    THEN 'Pending N+2'
                        WHEN 'LCIssued'                         THEN 'Issued'
                        WHEN 'PaymentDone'                      THEN 'Paid'
                        WHEN 'PaymentNotDone'                   THEN 'Unpaid'
                        WHEN 'Rejected'                         THEN 'Rejected'
                        WHEN 'Cancelled'                        THEN 'Cancelled'
                        ELSE s.application_status
                    END                                             AS HumanLabel,
                    COUNT(DISTINCT lrd.obj_id)                      AS Count,
                    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalAmount
                FROM lc_request_details lrd
                JOIN statuses        s  ON s.obj_id          = lrd.status_id
                LEFT JOIN lc_details ld ON ld.lc_request_id  = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@BankName     IS NULL OR UPPER(lrd.bank) LIKE '%' + UPPER(@BankName) + '%')
                  AND (@CustomerName IS NULL OR lrd.customer_id IN (
                        SELECT obj_id FROM customers
                        WHERE UPPER(name) LIKE '%' + UPPER(@CustomerName) + '%'))
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                GROUP BY s.application_status
                ORDER BY Count DESC"
            },

            // FIX: added RejectedCount, CancelledCount, ValidationCount, DraftCount.
            // TotalRequestedAmount as fallback. ISNULL on all aggregates.
            ["CustomerLC"] = new IntentDefinition
            {
                Name = "CustomerLC",
                Sql = $@"
                SELECT
                    c.name                                          AS CustomerName,
                    c.sap_sold_to_name                              AS SapName,
                    COUNT(DISTINCT lrd.obj_id)                      AS LcCount,
                    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue,
                    ISNULL(SUM(lrd.total_amount), 0)                AS TotalRequestedAmount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'LCIssued'
                                    THEN 1 ELSE 0 END), 0)         AS IssuedCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'PaymentDone'
                                    THEN 1 ELSE 0 END), 0)         AS PaidCount,
                    ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2')
                                    THEN 1 ELSE 0 END), 0)         AS PendingCount,
                    ISNULL(SUM(CASE WHEN s.application_status LIKE 'Submitted_For_Validation%'
                                    THEN 1 ELSE 0 END), 0)         AS ValidationCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'Draft'
                                    THEN 1 ELSE 0 END), 0)         AS DraftCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'Rejected'
                                    THEN 1 ELSE 0 END), 0)         AS RejectedCount,
                    ISNULL(SUM(CASE WHEN s.application_status = 'Cancelled'
                                    THEN 1 ELSE 0 END), 0)         AS CancelledCount
                FROM customers c
                JOIN lc_request_details lrd ON lrd.customer_id   = c.obj_id
                JOIN statuses           s   ON s.obj_id           = lrd.status_id
                LEFT JOIN lc_details    ld  ON ld.lc_request_id   = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@CustomerName IS NULL OR UPPER(c.name) LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@BankName     IS NULL OR UPPER(lrd.bank) LIKE '%' + UPPER(@BankName) + '%')
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                GROUP BY c.name, c.sap_sold_to_name
                HAVING COUNT(DISTINCT lrd.obj_id) > 0
                ORDER BY LcCount DESC"
            },

            // ── 9. SPECIFIC LC LOOKUP ─────────────────────────────────────────────

            ["LcStatus"] = new IntentDefinition
            {
                Name = "LcStatus",
                Sql = $@"
                SELECT
                    lrd.obj_id                                  AS RequestId,
                    lrd.bank                                    AS Bank,
                    lrd.product                                 AS Product,
                    lrd.volume                                  AS Volume,
                    lrd.business_line                           AS BusinessLine,
                    lrd.type_of_lc                              AS TypeOfLcRequested,
                    lrd.contract_number                         AS ContractNumber,
                    lrd.date_of_shipment                        AS PlannedShipmentDate,
                    lrd.lds                                     AS LatestDateOfShipment,
                    lrd.eta_date                                AS EtaDate,
                    lrd.total_amount                            AS RequestedAmount,
                    lrd.purchase_payment_term                   AS PurchasePaymentTerm,
                    lrd.suppliername                            AS SupplierName,
                    lrd.beneficiary                             AS Beneficiary,
                    lrd.port_of_destination                     AS PortOfDestination,
                    c.name                                      AS CustomerName,
                    c.sap_sold_to_name                          AS SapCustomerName,
                    bu.business_unit_name                       AS BusinessUnit,
                    s.application_status                        AS Status,
                    CASE s.application_status
                        WHEN 'Draft'                        THEN 'Draft'
                        WHEN 'Submitted_For_Validation'     THEN 'In Validation'
                        WHEN 'Submitted_N+1'                THEN 'Pending N+1'
                        WHEN 'Submitted_N+2'                THEN 'Pending N+2'
                        WHEN 'LCIssued'                     THEN 'Issued'
                        WHEN 'PaymentDone'                  THEN 'Paid'
                        WHEN 'PaymentNotDone'               THEN 'Unpaid'
                        WHEN 'Rejected'                     THEN 'Rejected'
                        WHEN 'Cancelled'                    THEN 'Cancelled'
                        ELSE s.application_status
                    END                                         AS StatusLabel,
                    ld.lc_number                                AS LcNumber,
                    ld.issuing_Bank                             AS IssuingBank,
                    ld.lc_issue_date                            AS LcIssueDate,
                    ld.lc_expire_date                           AS LcExpiryDate,
                    ld.grace_period                             AS GracePeriod,
                    ld.lC_expired                               AS IsExpired,
                    COALESCE(ld.amount, lrd.total_amount)       AS LcAmount,
                    ISNULL(ld.currency, lrd.currency)           AS Currency,
                    ld.qty_in_mt                                AS QuantityMt,
                    ld.tolerance_plus_in_percentage             AS TolerancePlus,
                    ld.tolerance_minus_percentage               AS ToleranceMinus,
                    ld.amount_tolerance_plus                    AS AmountTolerancePlus,
                    ld.amount_tolerance_minus                   AS AmountToleranceMinus,
                    ld.shipment_date                            AS ActualShipmentDate,
                    ld.payment_terms                            AS PaymentTerms,
                    ld.type_of_LC                               AS TypeOfLC,
                    ld.beneficiary_name_on_LC                   AS BeneficiaryOnLC,
                    ld.applicant                                AS Applicant,
                    ld.mill_name                                AS MillName,
                    ld.bank_address                             AS BankAddress,
                    ld.port_Of_loading                          AS PortOfLoading,
                    ld.port_Of_discharge                        AS PortOfDischarge,
                    ld.partial_shipment_allow                   AS PartialShipmentAllowed,
                    ld.period_for_presentation_days             AS PresentationDays,
                    ld.lc_amount_usd                            AS LcAmountUsd,
                    ld.usd_bank_charges                         AS UsdBankCharges,
                    ld.sap_order_number                         AS SapOrderNumber,
                    ld.ami_payment_date                         AS AmiPaymentDate,
                    ld.status_lc                                AS LcStatusDetail,
                    ld.comment                                  AS Comment,
                    (SELECT COUNT(*) FROM lc_amendment_details lad
                     WHERE lad.lc_request_id = lrd.obj_id)     AS AmendmentCount,
                    (SELECT COUNT(*) FROM invoice_details inv
                     WHERE inv.lc_request_id = lrd.obj_id)     AS InvoiceCount,
                    DATEDIFF(DAY, lrd.created_on, GETDATE())    AS DaysPending,
                    lrd.created_on                              AS RequestCreatedOn,
                    lrd.modified_on                             AS RequestLastModified
                FROM lc_request_details lrd
                JOIN statuses           s   ON s.obj_id          = lrd.status_id
                JOIN customers          c   ON c.obj_id          = lrd.customer_id
                JOIN business_unit      bu  ON bu.obj_id         = lrd.business_unit_id
                LEFT JOIN lc_details    ld  ON ld.lc_request_id  = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@LcNumber     IS NULL OR UPPER(ld.lc_number)       = UPPER(@LcNumber)
                                            OR UPPER(lrd.contract_number) = UPPER(@LcNumber))
                  AND (@MinAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) >= @MinAmount)
                  AND (@MaxAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) <= @MaxAmount)
                  AND (@BankName     IS NULL OR UPPER(lrd.bank)           LIKE '%' + UPPER(@BankName)     + '%')
                  AND (@CustomerName IS NULL OR UPPER(c.name)             LIKE '%' + UPPER(@CustomerName) + '%'
                                            OR UPPER(c.sap_sold_to_name)  LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
                  AND (@Country      IS NULL OR UPPER(ld.port_Of_loading)    LIKE '%' + UPPER(@Country) + '%'
                                            OR UPPER(ld.port_Of_discharge)   LIKE '%' + UPPER(@Country) + '%'
                                            OR UPPER(lrd.port_of_destination) LIKE '%' + UPPER(@Country) + '%')
                  AND (@DaysRange    IS NULL OR lrd.created_on >= DATEADD(DAY, -@DaysRange, GETDATE()))
                ORDER BY lrd.created_on DESC"
            },

            // ── 10. AUDIT / HISTORY ──────────────────────────────────────────────

            // FIX: added Bank, Product, Status columns; ISNULL fallback for LcNumber
            ["LcHistory"] = new IntentDefinition
            {
                Name = "LcHistory",
                Sql = $@"
                SELECT
                    al.obj_id                                   AS LogId,
                    al.action                                   AS Action,
                    al.log_type                                 AS LogType,
                    al.comment                                  AS Comment,
                    al.actioned_on                              AS ActionedOn,
                    u.first_name + ' ' + u.last_name            AS ActionedBy,
                    lrd.obj_id                                  AS RequestId,
                    ISNULL(ld.lc_number, lrd.contract_number)   AS LcNumber,
                    c.name                                      AS CustomerName,
                    lrd.bank                                    AS Bank,
                    lrd.product                                 AS Product,
                    s.application_status                        AS Status
                FROM lc_audit_log al
                JOIN lc_request_details lrd ON lrd.obj_id = al.lc_request_id
                JOIN users              u   ON u.obj_id   = al.actioned_by
                JOIN customers          c   ON c.obj_id   = lrd.customer_id
                JOIN statuses           s   ON s.obj_id   = lrd.status_id
                LEFT JOIN lc_details    ld  ON ld.lc_request_id = lrd.obj_id
                WHERE lrd.is_active = 1
                  AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                        SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId))
                  AND (@LcNumber     IS NULL OR UPPER(ISNULL(ld.lc_number,'')) = UPPER(@LcNumber)
                                            OR UPPER(lrd.contract_number)       = UPPER(@LcNumber))
                  AND (@CustomerName IS NULL OR UPPER(c.name) LIKE '%' + UPPER(@CustomerName) + '%')
                  AND (@DaysRange    IS NULL OR al.actioned_on >= DATEADD(DAY, -@DaysRange, GETDATE()))
                ORDER BY al.actioned_on ASC"
            }
        };

    // ── BaseQuery ─────────────────────────────────────────────────────────────
    // KEY FIXES:
    // • LcAmount  = COALESCE(ld.amount, lrd.total_amount)  — never NULL for pending LCs
    // • Currency  = ISNULL(ld.currency, lrd.currency)      — same reason
    // • StatusLabel CASE column on every row for frontend display
    // • DaysPending = DATEDIFF(DAY, lrd.created_on, GETDATE()) for urgency indicator
    // • MinAmount/MaxAmount filters use COALESCE so amount filter works on pending LCs
    // ─────────────────────────────────────────────────────────────────────────
    private static string BaseQuery(string condition) => $@"
        SELECT
            lrd.obj_id                                  AS RequestId,
            lrd.bank                                    AS Bank,
            lrd.product                                 AS Product,
            lrd.volume                                  AS Volume,
            lrd.business_line                           AS BusinessLine,
            lrd.type_of_lc                              AS TypeOfLcRequested,
            lrd.contract_number                         AS ContractNumber,
            lrd.date_of_shipment                        AS PlannedShipmentDate,
            lrd.lds                                     AS LatestDateOfShipment,
            lrd.total_amount                            AS RequestedAmount,
            lrd.suppliername                            AS SupplierName,
            c.name                                      AS CustomerName,
            c.sap_sold_to_name                          AS SapCustomerName,
            bu.business_unit_name                       AS BusinessUnit,
            s.application_status                        AS Status,
            CASE s.application_status
                WHEN 'Draft'                        THEN 'Draft'
                WHEN 'Submitted_For_Validation'     THEN 'In Validation'
                WHEN 'Submitted_N+1'                THEN 'Pending N+1'
                WHEN 'Submitted_N+2'                THEN 'Pending N+2'
                WHEN 'LCIssued'                     THEN 'Issued'
                WHEN 'PaymentDone'                  THEN 'Paid'
                WHEN 'PaymentNotDone'               THEN 'Unpaid'
                WHEN 'Rejected'                     THEN 'Rejected'
                WHEN 'Cancelled'                    THEN 'Cancelled'
                ELSE s.application_status
            END                                         AS StatusLabel,
            ld.lc_number                                AS LcNumber,
            ld.issuing_Bank                             AS IssuingBank,
            ld.lc_issue_date                            AS LcIssueDate,
            ld.lc_expire_date                           AS LcExpiryDate,
            ld.grace_period                             AS GracePeriod,
            ld.lC_expired                               AS IsExpired,
            COALESCE(ld.amount, lrd.total_amount)       AS LcAmount,
            ISNULL(ld.currency, lrd.currency)           AS Currency,
            ld.qty_in_mt                                AS QuantityMt,
            ld.shipment_date                            AS ActualShipmentDate,
            ld.payment_terms                            AS PaymentTerms,
            ld.type_of_LC                               AS TypeOfLC,
            ld.beneficiary_name_on_LC                   AS BeneficiaryOnLC,
            ld.port_Of_loading                          AS PortOfLoading,
            ld.port_Of_discharge                        AS PortOfDischarge,
            ld.lc_amount_usd                            AS LcAmountUsd,
            ld.ami_payment_date                         AS AmiPaymentDate,
            ld.status_lc                                AS LcStatusDetail,
            DATEDIFF(DAY, lrd.created_on, GETDATE())    AS DaysPending,
            lrd.created_on                              AS RequestCreatedOn
        FROM lc_request_details lrd
        JOIN statuses           s   ON s.obj_id          = lrd.status_id
        JOIN customers          c   ON c.obj_id          = lrd.customer_id
        JOIN business_unit      bu  ON bu.obj_id         = lrd.business_unit_id
        LEFT JOIN lc_details    ld  ON ld.lc_request_id  = lrd.obj_id
        WHERE lrd.is_active = 1
          AND (@UserId       IS NULL OR lrd.business_unit_id IN (
                SELECT business_unit_id
                FROM   user_business_unit_mapping
                WHERE  user_id = @UserId))
          AND (@LcNumber     IS NULL OR UPPER(ld.lc_number)          = UPPER(@LcNumber)
                                    OR UPPER(lrd.contract_number)    = UPPER(@LcNumber))
          AND (@MinAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) >= @MinAmount)
          AND (@MaxAmount    IS NULL OR COALESCE(ld.amount, lrd.total_amount) <= @MaxAmount)
          AND (@BankName     IS NULL OR UPPER(lrd.bank)              LIKE '%' + UPPER(@BankName) + '%')
          AND (@CustomerName IS NULL OR UPPER(c.name)                LIKE '%' + UPPER(@CustomerName) + '%'
                                    OR UPPER(c.sap_sold_to_name)     LIKE '%' + UPPER(@CustomerName) + '%')
          AND (@CurrencyCode IS NULL OR UPPER(ISNULL(ld.currency, lrd.currency)) = UPPER(@CurrencyCode))
          AND (@Country      IS NULL OR UPPER(ld.port_Of_loading)    LIKE '%' + UPPER(@Country) + '%'
                                    OR UPPER(ld.port_Of_discharge)   LIKE '%' + UPPER(@Country) + '%'
                                    OR UPPER(lrd.port_of_destination) LIKE '%' + UPPER(@Country) + '%')
          AND (@DaysRange    IS NULL OR lrd.created_on               >= DATEADD(DAY, -@DaysRange, GETDATE()))
        {condition}";

    /// <summary>
    /// Returns <c>true</c> when the supplied intent key exists in the internal
    /// predefined-SQL dictionary. Used by ChatService to make a deterministic
    /// routing decision before any SQL is executed.
    /// </summary>
    /// <param name="intent">Intent string returned by AiUnderstandingService.</param>
    public bool HasIntent(string intent)
        => !string.IsNullOrWhiteSpace(intent) && _intents.ContainsKey(intent);

    public IntentDefinition? GetDefinition(string intentName)
        => _intents.TryGetValue(intentName, out var def) ? def : null;

    public IntentDefinition GetFallbackDefinition()
        => _intents["LcStatus"];
}