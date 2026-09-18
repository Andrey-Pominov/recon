-- Reconciliation schema. Two sides of the same money, and the runs that compare them.
-- Nothing here is updated in place: a run is a snapshot, and what changed between runs
-- is a query over two snapshots.

-- Our side: clearings as the ledger announced them. Filled by following the ledger's outbox.
create table ledger_records (
    entry_id     uuid        primary key,
    ref          text        not null unique,     -- the entry's idempotency key: the reference the provider echoes back
    hold_id      uuid        not null,
    amount       bigint      not null check (amount > 0),
    currency     char(3)     not null,
    occurred_at  timestamptz not null,
    event_id     bigint      not null            -- outbox cursor this came from
);

-- Where we are in the ledger's outbox. One row.
create table ledger_cursor (
    singleton    boolean     primary key default true check (singleton),
    position     bigint      not null default 0,
    updated_at   timestamptz not null default now()
);
insert into ledger_cursor default values;

-- Their side: one report is one file, imported whole or not at all.
create table provider_reports (
    id           uuid        primary key,
    name         text        not null,
    sha256       bytea       not null unique,     -- the same file imported twice is the same report
    imported_at  timestamptz not null default now(),
    row_count    int         not null
);

create table provider_records (
    id             bigserial   primary key,
    report_id      uuid        not null references provider_reports(id),
    provider_ref   text        not null,
    our_ref        text        not null,
    amount         bigint      not null,
    currency       char(3)     not null,
    booked_at      timestamptz not null
);
create index provider_records_our_ref_idx on provider_records (our_ref);

-- A run compares everything known on both sides at that moment and records every discrepancy.
create table runs (
    id              uuid        primary key,
    started_at      timestamptz not null default now(),
    ledger_position bigint      not null,
    report_ids      uuid[]      not null,
    input_sha256    bytea       not null unique,   -- same inputs → same run, no second row
    matched         int         not null,
    discrepancies   int         not null
);

create type finding_category as enum (
    'amount_mismatch', 'currency_mismatch', 'date_mismatch',
    'missing_on_provider', 'missing_in_ledger', 'duplicate_on_provider'
);

create table findings (
    id               bigserial         primary key,
    run_id           uuid              not null references runs(id),
    category         finding_category  not null,
    ref              text              not null,
    ledger_amount    bigint,
    provider_amount  bigint,
    detail           text              not null
);
create index findings_run_idx on findings (run_id, category);
create index findings_ref_idx on findings (ref);

create or replace function reject_mutation() returns trigger language plpgsql as $$
begin
    raise exception 'reconciliation rows are immutable: % on %', tg_op, tg_table_name
        using errcode = 'restrict_violation';
end $$;

create trigger ledger_records_immutable   before update or delete on ledger_records   for each row execute function reject_mutation();
create trigger provider_reports_immutable before update or delete on provider_reports for each row execute function reject_mutation();
create trigger provider_records_immutable before update or delete on provider_records for each row execute function reject_mutation();
create trigger runs_immutable             before update or delete on runs             for each row execute function reject_mutation();
create trigger findings_immutable         before update or delete on findings         for each row execute function reject_mutation();

-- What changed between a run and the one before it. A discrepancy that was in the previous
-- run and is not in this one has resolved — usually because a late clearing arrived.
create view run_transitions as
with ordered as (
    select id, started_at, lag(id) over (order by started_at) as previous_id from runs
)
select o.id as run_id, o.previous_id,
       (select count(*) from findings f where f.run_id = o.id
          and not exists (select 1 from findings p where p.run_id = o.previous_id and p.ref = f.ref and p.category = f.category)) as opened,
       (select count(*) from findings p where p.run_id = o.previous_id
          and not exists (select 1 from findings f where f.run_id = o.id and f.ref = p.ref and f.category = p.category)) as resolved
from ordered o;
