namespace backend.Services.SqlGeneration;

/// <summary>
/// Provides the LC Trade Finance database schema prompt fragment used inside
/// the OpenAI system prompt. Contains exact column names from the real database.
/// Update this class when columns are added to the DB.
/// </summary>
public sealed class SchemaProvider
{
    // ─── COMPRESSED SCHEMA ────────────────────────────────────────────────────
    // Exact column names from the real database. ~900 tokens.
    // Every column the AI is allowed to reference is listed here.
    // ─────────────────────────────────────────────────────────────────────────
    private const string Schema = """
        ╔══════════════════════════════════════════════════════════════╗
        ║  LC TRADE FINANCE — SQL SERVER DATABASE SCHEMA               ║
        ║  All column names are EXACT. Use them verbatim in SQL.       ║
        ╚══════════════════════════════════════════════════════════════╝

        ━━━ TABLE: lc_request_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━
        Alias: lrd  — root record, created when a user submits an LC request.
        One lc_request_details → one lc_details (only after bank issues the LC).

          obj_id               INT          Primary key
          bank                 VARCHAR      Which bank (BNP / KBC / CACIB / COMMERZBANK)
          product              VARCHAR      Steel product name
          volume               DECIMAL      Volume in MT
          total_amount         DECIMAL      Requested LC amount (may differ from ld.amount)
          currency             VARCHAR      Requested currency (EUR/USD/AED/INR/AZN/CAD/DKK)
          status_id            INT          FK → statuses.obj_id
          customer_id          INT          FK → customers.obj_id
          business_unit_id     INT          FK → business_unit.obj_id
          created_by           INT          FK → users.obj_id
          modified_by          INT          FK → users.obj_id
          type_of_lc           VARCHAR      LC type as requested (UPAS / At Sight / USANCE)
          contract_number      VARCHAR      Internal contract ref
          lds                  DATE         Latest date of shipment (requested)
          date_of_shipment     DATE         Planned shipment date
          suppliername         VARCHAR      Supplier name
          beneficiary          VARCHAR      Beneficiary as per request
          port_of_destination  VARCHAR      Destination port
          purchase_payment_term VARCHAR     Payment terms (purchase side)
          sales_payment_term   VARCHAR      Payment terms (sales side)
          business_line        VARCHAR      Business line
          sales_incoterms      VARCHAR      Incoterms
          eta_date             DATE         Estimated arrival date
          lc_amount_usd        DECIMAL      LC amount in USD equivalent
          lc_amount_eur        DECIMAL      LC amount in EUR equivalent
          ami_payment_date     DATE         ArcelorMittal expected payment date
          submitted_for_approval_user_id INT FK → users.obj_id
          sales_manager_approver_id      INT FK → users.obj_id
          is_active            BIT          1 = active, 0 = deleted. ALWAYS filter: lrd.is_active = 1
          created_on           DATETIME     Request creation date
          modified_on          DATETIME     Last modified date

        ━━━ TABLE: lc_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: ld  — created when the bank issues the LC.
        ⚠️  MAY NOT EXIST for Draft / pending requests → ALWAYS LEFT JOIN.
        ⚠️  ld.amount and ld.currency can be NULL → always COALESCE/ISNULL.

          obj_id               INT          Primary key
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_number            VARCHAR      Bank-assigned LC number
          issuing_Bank         VARCHAR      Issuing bank name
          year                 INT          Year of issuance
          lc_issue_date        DATE         Date bank issued the LC
          lc_expire_date       DATE         Expiry date of the LC
          grace_period         DATE         Last date bank accepts documents
          lC_expired           BIT          Expiry flag — WARNING: often stale (0 even when expired).
                                            ALWAYS use DUAL condition:
                                            (ld.lC_expired=1 OR (ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date < GETDATE()))
          amount               DECIMAL      LC amount as issued by bank
          currency             VARCHAR      Currency as issued
          qty_in_mt            DECIMAL      Quantity in metric tonnes
          tolerance_plus_in_percentage  DECIMAL
          tolerance_minus_percentage    DECIMAL
          amount_tolerance_plus         DECIMAL
          amount_tolerance_minus        DECIMAL
          shipment_date        DATE         Actual shipment date
          shipment_month       VARCHAR      Shipment month label
          lds                  DATE         Latest date of shipment (actual)
          payment_terms        VARCHAR      Payment terms on LC
          type_of_LC           VARCHAR      LC type as issued (UPAS / At Sight / USANCE)
          beneficiary_name_on_LC VARCHAR   Beneficiary as per issued LC
          applicant            VARCHAR      Applicant name
          mill_name            VARCHAR      Mill / origin
          bank_address         VARCHAR      Issuing bank address
          port_Of_loading      VARCHAR      Port of loading
          port_Of_discharge    VARCHAR      Port of discharge
          partial_shipment_allow BIT
          period_for_presentation_days INT
          lc_amount_usd        DECIMAL
          lc_amount_eur        DECIMAL
          usd_bank_charges     DECIMAL
          sap_order_number     VARCHAR
          follow_up_number     VARCHAR
          ami_payment_date     DATE
          status_lc            VARCHAR
          comment              VARCHAR
          amendment_details    VARCHAR      HTML diff of latest amendment
          supplier_name        VARCHAR
          supplier_payment_date DATE
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ⚠️  NULL SAFETY for lc_details columns:
          Amount:   COALESCE(ld.amount, lrd.total_amount)         ← handles pre-issue nulls
          Currency: ISNULL(ld.currency, lrd.currency)             ← handles pre-issue nulls
          All date columns on ld are nullable — always add IS NOT NULL before comparisons.

        ━━━ TABLE: statuses  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: s

          obj_id               INT
          application_status   VARCHAR      EXACT values (case-sensitive):
                                            'Draft' | 'Submitted_For_Validation'
                                            'Submitted_N+1' | 'Submitted_N+2'
                                            'LCIssued' | 'PaymentDone'
                                            'PaymentNotDone' | 'Rejected' | 'Cancelled'

        ━━━ TABLE: customers  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: c

          obj_id               INT
          name                 VARCHAR      Customer display name
          sap_sold_to_name     VARCHAR
          sap_sold_to_nr       VARCHAR
          bpm_code             VARCHAR
          bussiness_line       VARCHAR      (typo in schema — use as-is)
          is_active            BIT
          scope                BIT

        ━━━ TABLE: business_unit  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━
        Alias: bu

          obj_id               INT
          business_unit_name   VARCHAR      Luxembourg / Singapore / India / LATAM
          description          VARCHAR
          is_active            BIT

        ━━━ TABLE: users  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: u

          obj_id               INT
          first_name           VARCHAR
          last_name            VARCHAR
          upn                  VARCHAR
          e_mail               VARCHAR
          dnd                  BIT
          is_active            BIT

        ━━━ TABLE: lc_amendment_details  [PK: obj_id] ━━━━━━━━━━━━━━━━
        Alias: lad

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_details_id        INT          FK → lc_details.obj_id
          lc_number            VARCHAR
          year                 INT
          issuing_bank         VARCHAR
          supplier_name        VARCHAR
          lc_issue_date        DATE
          lds                  DATE
          lc_expire_date       DATE
          grace_period         DATE
          lC_expired           BIT
          amount               DECIMAL
          currency             VARCHAR
          qty_in_mt            DECIMAL
          shipment_date        DATE
          payment_terms        VARCHAR
          type_of_LC           VARCHAR
          amendment_details    VARCHAR
          applicant            VARCHAR
          beneficiary_name_on_LC VARCHAR
          lc_amount_usd        DECIMAL
          lc_amount_eur        DECIMAL
          usd_bank_charges     DECIMAL
          ami_payment_date     DATE
          sap_order_number     VARCHAR
          comment              VARCHAR
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ━━━ TABLE: invoice_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━
        Alias: inv  — one row per invoice. JOIN on BOTH FKs.

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_details_id        INT          FK → lc_details.obj_id
          lc_number            VARCHAR
          qty                  DECIMAL
          shipment_date        DATE
          invoice_amount       DECIMAL
          currency             VARCHAR
          invoice_Date         DATE         ← capital D (exact name)
          beneficiary          VARCHAR
          beneficiary_pmt_date DATE         ← date beneficiary was actually paid
          ami_pmt_date         DATE
          is_Mark_as_paid      BIT          1 = paid, 0 = outstanding (exact name)
          is_Marked_as_final_update BIT
          is_Refunded          BIT
          Refund_Value_Date    DATE         ← capital R, V, D (exact name)
          lc_invoice_amount_usd DECIMAL
          usd_bank_charges     DECIMAL
          lc_invoice_amount_eur DECIMAL
          document_set_number  VARCHAR
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ━━━ TABLE: lc_approver_mapping  [PK: obj_id] ━━━━━━━━━━━━━━━━━
        Alias: lam

          obj_id               INT
          approver_id          INT          FK → users.obj_id
          lc_request_id        INT          FK → lc_request_details.obj_id
          status               NVARCHAR     'Close' = approved, 'Rejected' = rejected, NULL = pending
          is_approved_offline  BIT
          assigned_on          DATETIME
          action_taken_on      DATETIME     ← NULL means still pending

        ━━━ TABLE: lc_audit_log  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: al

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          actioned_by          INT          FK → users.obj_id
          action               NVARCHAR
          log_type             VARCHAR      'approval' | 'amendment' | 'invoice'
          actioned_on          DATETIME
          comment              VARCHAR

        ━━━ TABLE: user_business_unit_mapping  [PK: obj_id] ━━━━━━━━━━

          obj_id               INT
          user_id              INT          FK → users.obj_id
          business_unit_id     INT          FK → business_unit.obj_id
          created_on           DATETIME

        ═══════════════════════════════════════════════════════════════
        STANDARD JOIN PATTERN (base for every query):

          FROM lc_request_details lrd
          JOIN statuses      s   ON s.obj_id          = lrd.status_id
          JOIN customers     c   ON c.obj_id          = lrd.customer_id
          JOIN business_unit bu  ON bu.obj_id         = lrd.business_unit_id
          LEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id

        ═══════════════════════════════════════════════════════════════
        MANDATORY SCOPE FILTER (must appear in EVERY query):

          AND lrd.business_unit_id IN (
              SELECT business_unit_id
              FROM   user_business_unit_mapping
              WHERE  user_id = @UserId
          )

        ═══════════════════════════════════════════════════════════════
        BUSINESS RULES — always apply:

          R1. lrd.is_active = 1                       — filter deleted records
          R2. COALESCE(ld.amount, lrd.total_amount)   — handle pre-issue nulls
          R3. ISNULL(ld.currency, lrd.currency)       — handle pre-issue nulls
          R4. Expired: (ld.lC_expired=1 OR (ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date < GETDATE()))
          R5. ISNULL(SUM(...),0) and ISNULL(COUNT(...),0) and ISNULL(AVG(...),0) on all aggregates
          R6. Invoice joins: use BOTH inv.lc_request_id = lrd.obj_id AND inv.lc_details_id = ld.obj_id
          R7. (See TOP RULES section below — replaces old R7)
          R8. PascalCase aliases on all selected columns
          R9. NULL SAFETY: before any WHERE condition on a nullable column, add IS NOT NULL first.
              WRONG:   ld.grace_period < GETDATE()
              CORRECT: ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE()
        """;

    /// <summary>Returns the full database schema prompt fragment.</summary>
    public string GetSchema() => Schema;
}
