Repair the `SlugNormalizer.Normalize` implementation in `/workspace`.

The public contract is:

- Return lowercase invariant text.
- Replace every run of one or more non-ASCII-alphanumeric characters with one hyphen.
- Remove leading and trailing hyphens.
- Return an empty string when the input contains no ASCII letters or digits.
- Throw `ArgumentNullException` for a null input.

Keep the existing public API and make the project build successfully.
