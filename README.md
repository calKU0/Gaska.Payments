# Gaska.Payments

Money comes in from several places - the bank, the couriers who collect cash on delivery, and in
time a card acquirer. This service takes each of those, turns it into a cash entry in Comarch ERP XL
and settles it against the customer's open items. What it cannot settle by itself goes to a desktop
application where an accountant finishes it by hand.

> **Proprietary software.** See [LICENSE](LICENSE). Not to be copied or distributed.

---

## Contents

- [How it fits together](#how-it-fits-together)
- [The cycle](#the-cycle)
- [Bank statements](#bank-statements)
- [Cash on delivery](#cash-on-delivery)
- [Posting to ERP](#posting-to-erp)
- [The accounting application](#the-accounting-application)
- [Configuration](#configuration)
- [Database](#database)
- [Logging](#logging)
- [Running it](#running-it)

---

## How it fits together

| Project | What it is |
| --- | --- |
| `Gaska.Payments.Domain` | the matching engine and the models it works on - pure logic, no database and no network ([details](Gaska.Payments.Domain/README.md)) |
| `Gaska.Payments.Erp` | everything Comarch: the XL API (`Api/`), reads from `CDN.*` (`Reads/`), the posting journal (`Posting/`) |
| `Gaska.Payments.Integrations` | the outside world: the bank's web service, the courier mailbox and the readers of their report formats, the file archive |
| `Gaska.Payments.Application` | the use cases: the bank cycle, the courier cycle, posting, the stores and the schema |
| `Gaska.Payments.Service` | the Windows service: the host and the cycle timer |
| `Gaska.Payments.Desktop` | the accounting application (`RozliczaniePrzelewow.exe`) |

Dependencies run one way only:

```
Domain ← Erp, Integrations ← Application ← Service
Domain, Erp ← Desktop
```

Anything that talks to the outside world sits on one side of that line, anything that decides what
to do sits on the other. That is what lets the courier cycle and the bank cycle live side by side
instead of tangled together, and it is where a card acquirer will slot in.

**Everything that touches the XL API must be x86.** The Comarch client library is 32-bit, so the
service, the desktop application and the projects between them are all built for x86. The library
itself only loads from an installed ERP XL client directory, which has to be on `Path`.

---

## The cycle

The service wakes on a timer and does the same four things:

1. **Archive** yesterday's PDF statements from the bank.
2. **Fetch and match** - download bank operations, match each against the customer's open items,
   write a proposal with a confidence.
3. **Collect** the courier payout reports waiting in the mailbox.
4. **Post and settle** - create the cash entries in ERP and close the open items of everything the
   engine considers certain.

A failure in any one of those is logged and the rest of the cycle carries on. Reprocessing the same
period is safe; that is what makes an outage a delay rather than a problem.

---

## Bank statements

Operations come from the bank's web service as camt.052. Alongside them the service downloads the
bank's own **PDF statement** for each closed day and files it in the archive, so an accountant can
open the document behind any entry.

Two things about the statement call are worth knowing, both established against the live service:

- **"no statement" arrives as an error, not as an empty answer.** A weekend, a holiday or an account
  outside the agreement all answer with a fault. None of them is a problem, so they are recorded at
  debug level only - otherwise every weekend would produce warnings on every pass.
- **a statement already held for a later day proves an earlier day's absence is final.** Without that
  rule the archive would ask about every past Saturday forever.

Accounts are never listed in configuration. What is configured is **register symbols**; the account
numbers come from the registers themselves in ERP, because the register the entries go to and the
account the statement comes from are by definition the same row. The bank wants a full IBAN with the
country code, which ERP stores in a separate column, so the number is assembled when the register is
read.

An account the bank refuses is skipped with a warning and the cycle carries on. When every account
fails, that is an outage rather than a configuration problem and the exception is allowed to
propagate.

---

## Cash on delivery

Parcels sent collect on delivery are paid by the customer to the courier, and the courier pays us
once a day for everything it has collected. Each payout arrives as one transfer and, by e-mail, as a
report breaking it down parcel by parcel. Each parcel becomes one cash entry - a parcel is one
customer paying for one invoice, and it is the invoice that has to be closed.

### Three formats

Each courier has a format of its own, and only one of them is what its file extension claims.

| Courier | What arrives | Where the document number is |
| --- | --- | --- |
| A | a semicolon-separated CSV; one summary line, then the parcels | a column of its own |
| B | a real Excel workbook in the old binary format | inside a free-text description |
| C | an HTML table in a MIME envelope, named `.xls` | a description column, repeated in the notes |

A report is recognised by the shape of its attachment rather than by who sent it, so a change of
sending address does not stop the service; the sender list only narrows down which messages are
looked at.

In all three the parcels add up to the declared transfer to the penny - the couriers deduct no fees
from what they collect. That is checked on every file, and a report that does not add up produces no
entries at all: it means we have read the file wrongly, and posting off a misread file is worse than
posting nothing.

### Somebody else's money

One courier offers a service where it pays the **customer** rather than us, and sends us the report
all the same. Such a report is shaped exactly like ours; the only thing that tells them apart is the
account at the head of the file. So each courier lists the accounts it may pay into, and a report
naming any other account is archived but produces no entries.

### Nothing happens until the money does

A report is acted on when the transfer arrives, not when the file does. The entries carry the day the
transfer was booked, and that day is nowhere in the file - one courier declares a date and pays the
day after, another sends its file days in advance. So a report is archived, recorded as waiting, and
taken up again on whichever later pass finds its transfer.

The transfer is found among the credits on the bank registers:

- **by the courier's payment reference** in the title, where the courier gives one. It is matched
  whole, letters included, and only where it is not part of a longer number - reduced to digits it
  is far too weak and once matched a report to an unrelated payment an order of magnitude smaller.
- **by the waybills** the title lists, which is all the other two give. Those are looked for among
  the digits alone, because the bank breaks a title every 35 characters and ERP stores the pieces run
  together, cutting numbers in half. One waybill is not enough on its own: two are needed, or one on
  a transfer worth exactly what the report adds up to.

**The transfer has to be worth exactly what the report adds up to.** When it is not, the entries are
still created, dated by the transfer, but nothing from that report is settled automatically and the
accounting team is written to - once per report, not once an hour.

### Penny for penny

A parcel is settled automatically only when its amount equals what is left on the documents it points
at, to the penny. Anything else is posted and left for an accountant, with both figures written onto
the row. A part settlement nobody asked for is worse than an open item somebody looks at.

### The courier's own transfer

Once every parcel of a report has been settled, the collective transfer that paid for them is flagged
as not subject to settlement and told where the money went. The same cash is accounted for parcel by
parcel, so leaving it open would leave a large unidentified receipt in the queue for ever.

All or nothing: one parcel left unsettled means the transfer still has something to answer for, and
it is left exactly as it is.

---

## Posting to ERP

Entries are created through the XL API. Three rules were established the hard way and each is worth
knowing before changing anything here.

### An entry only ever goes into the newest report

ERP takes a cash entry into the register's **most recent** daily report and no other. A report from an
earlier day refuses it even when that report is wide open. Neither the code for that nor the code for
a closed report appears in ERP's message table, so both were established by experiment against the
live API.

**This is why the daily reports are created one at a time, each immediately before the entries of its
own day.** They used to be created all at once, ahead of the posting loop, which quietly stranded
every operation of the days before: the bank delivers some operations a day late, and by the time
they arrived the register had already moved on.

An operation whose register has already moved past its day cannot be posted at all, so it is left out
of the posting query rather than retried every hour forever, and reported once per pass instead.

### Never twice

An operation must reach ERP once, and two things can put it there: this service, and a person - the
accountants enter operations by hand, and ERP's own statement import writes its own numbering.
Neither fills in the field where we keep the bank's reference, so matching on that alone sees nothing
and posts a second entry.

So an operation is recognised by what the bank actually sent: register, day, direction, amount, and
the payment title with its whitespace removed and cut short. That key is not unique - identical bank
commissions arrive several a day, word for word alike - so the two sides are numbered within each
group and **paired off one to one**: the n-th of ours takes the n-th unclaimed entry. Whichever way
round they pair makes no difference, because within a group they are indistinguishable; what matters
is that four of ours can never all claim one entry, and that three entries leave one still to post.

Nothing in the posting query guards against duplicates, and nothing needs to: the pairing runs first
on every pass, and an operation it has claimed is no longer visible to it.

### Idempotence

Every write is keyed on something stable, so the same period can be processed again without
consequence. Cash entries are recognised again after a crash, settlements are recorded as they
happen rather than at the end, and a proposal the operator has ruled on is never overwritten by
another run of the engine.

### Two direct writes

Two things cannot be done through the XL API - there is no function that modifies an existing cash
entry - and are done with SQL instead, each documented where it happens:

- linking the two legs of a split payment;
- flagging a courier's collective transfer as not subject to settlement.

Both touch only entries that are still untouched, and both were agreed with the system's owner.

---

## The accounting application

A WPF application used to settle by hand what the service did not consider certain. Everyone signs
in with their own ERP operator, so a settlement carries a record of who created it.

Its queue is driven by the cash entries in ERP, with the service's proposals joined on from the
outside - not the other way round. An entry ERP's own import created has no proposal, and that is
every entry on the card registers; it still has to be visible.

**Which registers appear** comes from the application's own configuration, so a register with nothing
on it today is still on the filter rather than silently absent. **Which of them start ticked** is a
separate setting: the two used to be one, which made it impossible to say "settle here, but do not
open the window on it".

**What the signed-in operator may see** comes from ERP. Rights to registers hang on the operator's
**centre**, not on the operator, and ERP has a function of its own for the question - the application
calls it rather than reimplementing the rule, so it cannot drift from what the Comarch client shows
the same person. A register the centre does not reach leaves the filter without a word on screen.

Every entry carries a button that opens the document behind it: the bank statement, or the courier
report a collection came from. It appears only when the file is actually in the archive.

---

## Configuration

Everything is in `appsettings.json`, in the `Serilog`, `Erp`, `Bnp`, `Archive`, `Cod`, `Settlement`
and `Xl` sections. Each program holds only what it uses - the desktop application knows neither the
bank credentials nor the ERP operator's password, because it signs in through the ERP window.

**The real `appsettings.json` is not in the repository**; it carries the database credentials, the
bank certificate password and the mailbox password. Copy `appsettings.example.json`, fill it in, and
keep it out of source control. Before a production deployment move the secrets to environment
variables or `dotnet user-secrets` - the projects already carry a `UserSecretsId`.

Registers are configured in three lists: the ones that are settled, the card accounts, and the
auxiliary accounts that are only posted to. The cash on delivery register is separate again, because
nothing is downloaded from the bank for it.

---

## Database

The service's own tables live in the **`pay`** schema - `pay.Run`, `pay.Payment`, `pay.Allocation`,
`pay.CourierReport` and the `pay.Queue` view. The database belongs to Comarch ERP XL and its `dbo` is
a crowded place; a schema of our own means nothing can collide, it is obvious which tables are ours,
and rights can be granted over the lot in one statement.

The schema is created and migrated at startup, so deployment is copying files - there is no separate
migration step. An installation created by an earlier version is moved across on the first start,
rows and all.

Reads from `CDN.*` are ordinary; writes go to `pay.*` and to the two documented exceptions above.

---

## Logging

Both programs log through Serilog, configured entirely from their own `appsettings.json`. **The log
is in English**, all of it - the Polish is in the interface, where the people are.

| | Service | Application |
| --- | --- | --- |
| everything | `logs/automat-<date>.log` | `logs/rozliczanie-<date>.log` |
| warnings and errors only | `logs/automat-errors-<date>.log` | `logs/rozliczanie-errors-<date>.log` |
| console | at `Information` | - |

Rolled daily and again at 50 MB. The second file exists so a problem can be found without reading
past tens of thousands of ordinary lines.

Steps worth timing are wrapped in `TimedOperation`, which writes one line when a step starts and one
when it ends, with the elapsed milliseconds as a property of its own rather than buried in the text:

```
[INF] Run 60: downloading from the bank finished in 12946 ms: … operations from … accounts
[INF] Building the document indexes finished in 1564 ms: … receivables, … liabilities
[INF] Run 60: matching finished in 475 ms: … operations run through the engine
```

That is not decoration - it is how the slowest step in the cycle was found, and it was not the one
anybody would have guessed.

---

## Running it

```bash
dotnet run --project Gaska.Payments.Service
```

Requires an installed Comarch ERP XL client with its directory on `Path`, and a filled-in
`appsettings.json`.

### As a Windows service

```bash
dotnet publish Gaska.Payments.Service -c Release -o C:\Serwisy\GaskaPayments
```

```bash
sc.exe create GaskaPayments binPath= "C:\Serwisy\GaskaPayments\Gaska.Payments.Service.exe" start= auto
```

The account the service runs as needs read access to the ERP client directory and rights on the
`pay` schema.

### The accounting application

Built to `Gaska.Payments.Desktop\bin\Release\net10.0-windows\RozliczaniePrzelewow.exe`. It needs the
same ERP client. The interface is in Polish, because that is the language the accounting team works
in; the code, its comments and this document are in English.
