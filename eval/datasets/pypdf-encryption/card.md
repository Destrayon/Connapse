# pypdf-encryption

- **What it tests:**
  - Encrypted PDFs that need a password must fail loudly: rejected, or Failed with a reason.
  - Encrypted PDFs that open without one must still have their text extracted.
- **Source:** https://github.com/py-pdf/pypdf/tree/54d35184b9e984834823b3558dc096bd4e6c9e80/resources/encryption. These are pypdf's own encryption test files, covering revisions R2 to R6 and RC4 and AES.
- **Upstream licence:** pypdf is BSD-3-Clause. The files are downloaded at run time and not committed.
- **Size:** 17 PDFs, one page each, all carrying the text "pdf encryption test / password: asdfzxcv".
- **Expected outcomes:**
  - **Fail loudly (`password-required`):** every `*-user-password.pdf`, `r6-both-passwords.pdf`, and `r4-aes-v2-no-key-length.pdf`. That last file's name doesn't say it needs a password; pypdf 6.1.1 cannot open it with an empty password.
  - **Extract (`readable`):** every `*-empty-password.pdf`, every `*-owner-password.pdf`, and `unencrypted.pdf`. Each gets a `present` check for "pdf encryption test".
- **Known limitations:** single-page and tiny, so the set tests decryption, not layout.
