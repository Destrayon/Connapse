# pypdf-encryption

- **What it tests:**
  - Encrypted PDFs that need a password must fail loudly: rejected, or Failed with a reason.
  - Encrypted PDFs that open without one must still have their text extracted.
- **Source:** https://github.com/py-pdf/pypdf/tree/54d35184b9e984834823b3558dc096bd4e6c9e80/resources/encryption. These are pypdf's own encryption test files, covering revisions R2 to R6 and RC4 and AES.
- **Upstream licence:** pypdf is BSD-3-Clause. The files are downloaded at run time and not committed.
- **Size:** 17 PDFs, one page each, all carrying the text "pdf encryption test / password: asdfzxcv".
- **Expected outcomes:** decided by whether pypdf 6.1.1, with AES support installed, opens the file with an empty password. The file names do not decide it.
  - **Fail loudly (`password-required`, 6 files):** `r2-user-password`, `r3-user-password`, `r4-user-password`, `r4-aes-user-password`, `r4-aes-v2-no-key-length` and `r6-both-passwords`.
  - **Extract (`readable`, 11 files):** every other file. That includes `r5-user-password` and `r6-user-password`, whose owner password is empty, so any reader opens them without a prompt. Each gets a `present` check for "pdf encryption test".
- **Known limitations:** single-page and tiny, so the set tests decryption, not layout.
