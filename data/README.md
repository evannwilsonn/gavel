# Data

Gavel uses the **Blue Book for Bulldozers** data from the 2013 Kaggle competition run by Fast Iron
(https://www.kaggle.com/c/bluebook-for-bulldozers/data): auction sales of used heavy equipment in the US,
with the machine's description, usage, the sale date and place, and the hammer price.

The files are **not in this repository**. Kaggle's competition rules don't allow redistributing the data,
so `scripts/get-data` fetches them from the copy in the Zero to Mastery course repo
(`data/bluebook-for-bulldozers.zip` in [mrdbourke/zero-to-mastery-ml](https://github.com/mrdbourke/zero-to-mastery-ml)),
extracts the three files below into this folder, and deletes the zip. Set `ZTM_REPO` to a local clone of the
course repo to skip the download.

| File | Rows | What it is |
|---|---|---|
| `Train.csv` | 401,125 | Sales from 1989 to 31 Dec 2011, with `SalePrice`. Everything the models learn from. |
| `Valid.csv` | 11,573 | Sales from 1 Jan to 30 Apr 2012, without prices. Gavel's held-out set. |
| `ValidSolution.csv` | 11,573 | The prices for `Valid.csv` (joined on `SalesID`). Read once, by `holdout`. |

The zip also has `Test.csv` (May to Nov 2012), but its prices were never published in the course's copy,
so Gavel doesn't use it.

Expected SHA-256 of the extracted files:

```
a426a4965c1378b48360280b9c3fdd637b42e67601409ab11511c28e9fe6e776  Train.csv
c5d8d74447ed16b293358516dec7f1fd930b22a7a71159b94a61e92aa1d08ece  Valid.csv
8361e31660ae73997b1741ecba7bdad37bcdda3711fb1adb2a9d1d4df7c5c2b0  ValidSolution.csv
```

## How the columns are read

- `saledate` (`11/16/2006 0:00`) becomes year, month, day, day of week and day of year, as in the course,
  plus a continuous "years since 1989" trend and the machine's age at sale (both tested; see the README).
- `YearMade = 1000` is the dataset's placeholder for "unknown" (about 10% of rows) and is treated as missing.
- `MachineHoursCurrentMeter` is blank or 0 for 83% of rows; both count as "not recorded".
- Every text column is a category. `"None or Unspecified"` is kept as its own value, separate from blank.
- `SalesID` and `MachineID` are identifiers and are left out (tested both ways). `ProductGroupDesc` repeats
  `ProductGroup` and is dropped.

## What ends up in docs/

The demo needs category names to build its form (`docs/catalog.json`: model names, size classes, cab types
and their counts) and shows 36 real 2012 sales from `Valid.csv` for "Pull a real 2012 sale"
(`docs/examples.json`: a few fields per sale and the price). These are small derived excerpts used to
demonstrate the model, not the dataset.
