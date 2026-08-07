# Security policy

## Supported release

Security fixes target the latest published release.

## Reporting

Do not include OpenAI API keys, logs containing sensitive information, cache archives, or game files in public issues. Open a private GitHub security advisory for credential exposure or a vulnerability that could affect local data or process safety.

## Local data

`openai-api-key.txt`, `cache/`, and `logs/` are local-only and ignored by Git. Revoke any API key that is accidentally exposed.
