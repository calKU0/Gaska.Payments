# Matching payments to invoices

This module ties an operation from a bank statement (the bank's web service (camt)) to an open item in
Comarch ERP XL. It writes nothing to the database - it returns a settlement proposal with a
confidence level and a justification.

## Why this is not a plain `LIKE '%number%'`

A sample of several hundred real payment titles from `CDN.Zapisy.KAZ_TrescCDC` shows that
customers write the same document - `(S)FS-10548/26/SPR` - in all of these ways:

| variant | example from production |
|---|---|
| the full number | `(S)FS-10548/26/SPR` |
| another separator | `(S)FS-25424_26_SPR`, `(S)FS.31699/26/SPR`, `(S)FS-27622 26 SPR` |
| no separators | `SFS-2636126SPR`, `FSE226826WDT`, `FS18617/26/SPR` |
| no separators and no symbol | `PAGO PROFORMA 5873626S` (order ZS-58736/26/S) |
| a space as the separator | `CV FACTURA NR 57134 26 S` |
| the order number instead of the invoice | `PRO-FORMA INVOICE NO: ZS-57721/26/S` |
| a list sharing one ending | `FS-17887,18222/26/SPR`, `SFS-32075, 32431, 32735, 33266/26/SPR` |
| no document symbol | `25508/26/SPR`, `24211/26`, `29574, 30100, 30650` |
| keyword plus number | `za FV 23622`, `F.18645,18698,19149` |
| a digit lost | `ZAPŁATA ZA FV 3633,4080,4584,6363` (meaning 23633, 24080 and so on) |
| a KSeF number | `1234567890-20260710-84EE4E400000-8C` |
| a split payment message | `/VAT/154,17/IDC/1234567890/INV/(S)FS-10411/26/SPR/TXT/…` |
| per-document amounts | `FS-10137/26/SPR (1 521,60 PLN) FS-10274/26/SPR (357,14 PLN)` |
| no hint at all | `ZAPŁATA FV`, `Zaległe faktury`, `SPŁATA FAKTUR` |

On top of that the bank assembles the description from 35-character lines, so a number is
sometimes cut at a random place: `(S)FS-23 069/26/SPR`, `26/S PR`, `677-000-03- 35`.

## How it works

### 1. Normalisation (`TextNormalizer`)

The text is analysed **twice**, because neither form is sufficient on its own:

* **without spaces** - this glues back numbers cut by the 35-character wrapping, but loses word
  boundaries (`INVOICE FSE-4760/26` becomes one run),
* **with spaces** - the word boundaries are real, but cut numbers stay cut.

From the second pass we take only references that carry a year: a cut number has no year, so false
positives cannot slip through.

### 2. Taking the title apart (`DescriptionParser`)

First the fragments that are *not* document numbers are masked out - dates, amounts, account
numbers, the tax id from `/IDC/`, KSeF identifiers, tokens mixing letters with digits. Only then
are references extracted from what is left, strongest first:

| strength | what it means |
|---|---|
| `FullNumber` | symbol, number, year and a known series |
| `KindNumberYear` | symbol, number and year |
| `KindAndNumber` | symbol and number |
| `InvoiceKeyword` | a number after a word such as "FV", "faktura" or "przelew" |
| `ListContinuation` | a further element of a list (`FS-17887,18222/26/SPR`) |
| `BareNumber` | a run of digits on its own |

A year and series given once at the end of a list are propagated to all of its elements.

### 3. The document index (`DocumentIndex`)

Open payments from `CDN.TraPlat` and `CDN.TraNag` are indexed at four levels of precision
(`kind+number+year`, `kind+number`, `number+year`, `number`), additionally by the numbers of
related documents and by the KSeF number from `CDN.KSeFDokumenty`. Separate dictionaries hold
`account to contractor` (`CDN.RachunkiBankowe` and `CDN.NumeryRachunkow` with `ObiTyp = 32`) and
`tax id to contractor`.

Related numbers are the goods issues an invoice collects and the orders those issues fulfil. The
route through goods issues is necessary because a collective invoice (`TrN_SpiTyp = 0` - 24
thousand sales invoices and 4 thousand export invoices a year here) **carries no order on its
header**; the link sits on the issues instead. Orders are read from the issue's line items
(`CDN.TraSElem.TrS_Rez*`) rather than from the goods issue header, because the header holds a
single order while one issue can fulfil a dozen - such issues are common, and the record holder
covers several dozen orders.

A hit through a related number follows rules of its own: the reference in the title describes **the
order, not the invoice**, so neither kind nor series is compared against the invoice.
`ZS-57656/26/S` has by design a different kind and series from the `(S)FSE-5366/26/WDT` it
produced - the comparison is made against the order.

KSeF numbers are compared after reduction to alphanumeric characters alone - customers drop the
hyphens, and the bank cuts the number into lines on top of that.

A hit's score depends on the level the number was found at, and is lowered when the number matches
several documents, when the document belongs to a contractor other than the sender of the transfer,
or when the series does not agree.

### 4. Establishing the contractor (`PaymentMatcher`)

In turn, from the strongest evidence to the weakest:

1. the contractor already recorded on the ERP entry,
2. the payer's account (`CDN.RachunkiBankowe` / `CDN.NumeryRachunkow`, `ObiTyp = 32`),
3. the tax id from a split payment message,
4. an unambiguous document number from the payment title,
5. a vote, when several bare numbers from the title point at the same contractor
   (`4307+3952+4036+4110`),
6. the payer name sent by the bank.

The order matters: the name is consulted **last**, because it is the only imprecise piece of
evidence. Were it to come before the numbers, two companies with similar names could bury an
unambiguous invoice number given in the title.

Knowing the contractor is decisive: only then can bare numbers, and numbers with a digit lost, be
used safely.

### 4a. Recognition by payer name

Names from `Knt_Nazwa1`, `Knt_Nazwa2` and `Knt_Akronim` are split into tokens with no diacritics,
no punctuation and **no legal forms or filler words** (`SP. Z O.O.`, `S.A.`, `SIA`, `UAB`, `KFT`,
`SRL`, `FIRMA HANDLOWO-USŁUGOWA`, `GOSPODARSTWO ROLNE` and so on) - in the name "Firma
Handlowo-Usługowa Jan Kowalski" the information is carried by the surname alone.

Matching runs first on the complete set of tokens and, failing that, on their intersection (at
least 67% of the shorter name). The safeguards against a chance hit:

* only contractors that have documents in the index are recorded,
* a token shared by more than 150 contractors is not taken as a lead,
* **two** shared tokens are required; one suffices only when it is long and unique across the whole
  database (`AGROLATGALE`),
* on a tie between two contractors we name nobody.

The payer name from BNP arrives glued to the address (`ODM TECH S.C.|WARSZAWSKA 65`) - only the
first segment is used.

### 5. Balancing the amount

The strategies, in the order they are tried:

1. **`ExplicitReferencesExactSum`** - the documents named in the title add up to the amount. This
   covers bulk payments that list their invoices, and corrections (which enter with a minus sign).
2. **per-document amounts from the title** - where the customer wrote out how much goes to which
   invoice.
3. **`SingleDocumentPartialPayment`** - one document named, the payment covering part of it.
4. **`ExplicitReferencesPartial`** - the customer listed more documents than they paid for; we look
   for a subset of the named ones that balances.
5. **`ReferencesExtendedBySubsetSum`** - the named documents plus further open items of the
   contractor.
6. **`SubsetSumOnContractor`** - the title says nothing, but exactly one set of the contractor's
   open items comes to the amount.
7. **`OldestFirstFallback`** - spreading over the oldest open item first. **Off by default**
   (`MatchingOptions.EnableOldestFirstFallback`): on historical data it never once hit (0 out of 25
   payments) while looking in the queue like a finished proposal.

The guesses in points 6 and 7 are **switched off** when the customer gave a specific document
number in the title and it is not among the open items. Such a payment goes to "needs explaining"
along with the number the customer quoted. Substituting an arbitrary invoice of the right amount in
that situation is worse than no proposal - it happens, for instance, when the same invoice is paid
twice, and the accounting team has to make that call.

The subset search (`SubsetSumSolver`) works in minor units, handles negative values (a correction -
"six invoices less a credit note" is an ordinary subset to the solver), prunes branches by suffix
sums and has a hard cap on visited nodes. The pool is the 40 oldest open items of the contractor in
the given currency, and a proposal holds at most 25 documents.

**When several different sets give the same amount, none is proposed.** On historical data an
unambiguous solution was 95% accurate (80 exact out of 84 payments) and an ambiguous one 18% (26
out of 142) - naming one of many was a coin toss. The operator is told how many sets fit instead.

### 6. Confidence levels

| level | meaning |
|---|---|
| `High` | unambiguous reference and a matching amount - fit for the automat |
| `Medium` | the amount balances but the evidence is weaker - for the operator to approve |
| `Low` | a guess (FIFO spreading, an ambiguous subset) - a hint only |
| `None` | needs explaining |

`High` requires **two independent confirmations**:

* **who paid** - the sender has to be recognised by an account number attached to the contractor in
  ERP. That is the only evidence that comes from the bank rather than from what the customer typed
  into the title. Recognition by invoice number, tax id or payer name suffices for a proposal, but
  not for posting without a human
  (`MatchingOptions.RequireBankAccountForHighConfidence`),
* **what to settle** - the amount agreeing to the penny, rather than the format of the number in
  the title: with a perfectly matching amount "F.32502" is as certain as the full
  "(S)FS-10685/26/SPR".

What does not reach the automat are hits with a caveat (`ReferenceHit.Reliable`): a number
belonging to a contractor other than the sender, a series that does not agree, a number matching
several documents, a number with its leading digit lost. A part payment also stays at `Medium`
unless the document was named by its full number - there the amount confirms nothing.

## Accuracy

Measured over two months of account credits taken straight from the bank, across accounts in four
currencies. The input is only what the bank sends: title, amount, currency, date, payer name and
payer account.

| confidence | share |
|---|---|
| High | 84.4% |
| Medium | 3.2% |
| Low | 7.1% |
| None | 5.3% |

A contractor is established for around 98% of operations - most often from the payer's account,
then from a document number in the title, the payer name and the tax id from a split payment
message.

For those operations where ERP already holds a settlement made by the accounting team, the engine's
decisions can be compared against it:

| confidence | accuracy | coverage |
|---|---|---|
| High | 83.5% | 99.0% |
| Medium | 69.1% | 92.7% |
| Low | 28.1% | 57.4% |

*Accuracy* is the share settled exactly as the accountant did; *coverage* is the share where the
engine picked the right documents, whether in full or in part. Wrong proposals - the engine choosing
documents the accountant did not - are well under one percent of everything it looks at.

The engine would rather say "I do not know" than guess:

The errors in the `High` bucket amount to 0.65% of the proposals and, having reviewed every one of
them, not a single misread number. They are cases where the customer named invoice X, the amount
agrees to the penny, and the accounting team posted the payment against other invoices, usually the
oldest ones.

*exact* means the same set of documents as the accounting team chose; *coverage* is the share of
the documents they settled that the engine also named.

Notes on the method:

* an operation from the bank is tied to an ERP entry by amount, currency, date and title similarity
  (`BankToErpCorrelator`) - ERP does not keep the bank's reference for an operation,
* the technical documents generated during settlement were excluded from the comparison (`435` an
  exchange difference, `434` a netting, `784` a cash entry) - they cannot be predicted from a
  payment title,
* the test uses the documents' full amounts, because the historical balances of "what was open on
  the day of payment" cannot be reconstructed; hence some of the "partial" results in the `High`
  bucket are cases where the engine added an open credit note and the accounting team settled it
  separately,
* the errors from the `High` bucket, reviewed by hand, are almost exclusively cases where the
  accounting team settled the payment against invoices other than the ones the customer quoted.

## Running it

The engine is a library with no dependency on the database or the bank - the
`AutomaticSettlementOfBNPPayments` service calls it in its cycle. For configuration and installing
the service see the [solution README](../README.md).

The accuracy measurement was built for tuning the rules: it downloaded operations from the bank,
matched them against all documents (settled ones included) and compared the result with what the
accounting team decided. It lived in a separate tool that went away along with the other run modes -
the numbers above stand as a point of reference.
