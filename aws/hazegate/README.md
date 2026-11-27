# HazeGate on AWS

The stack creates an API Gateway HTTP API, a Node.js Lambda function and a Secrets Manager bearer token in the selected Region. The Lambda execution role only needs its basic logging permissions. The function uses HazeGate v1 for pages, resources, redirects and cookies. It checks resolved addresses before connecting and on each bundled-resource redirect.

## Deploy in Sydney

With the AWS CLI and AWS SAM CLI installed, sign in to the intended project. Run these commands from this directory with deployment permissions for CloudFormation, SAM artifacts in S3, Lambda, IAM, API Gateway and Secrets Manager.

```sh
aws login --profile hazegate-cloud-compute --region ap-southeast-2
sam deploy --template-file template.yaml --stack-name hazegate --resolve-s3 --capabilities CAPABILITY_IAM --region ap-southeast-2 --profile hazegate-cloud-compute
```

The stack outputs the endpoint and the ARN of the generated client token. API Gateway and Lambda charge for usage. Secrets Manager charges for the stored secret.

## Connect PhantomHaze

On Windows, a project operator can use the AWS CLI from PowerShell to configure the current Windows user. The profile must be signed in on that computer. Run with access to the stack outputs and the secret.

```powershell
$target = @('--region', 'ap-southeast-2', '--profile', 'hazegate-cloud-compute')
$outputs = aws cloudformation describe-stacks --stack-name hazegate --query 'Stacks[0].Outputs' --output json @target | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not read the HazeGate stack.' }
$endpoint = ($outputs | Where-Object OutputKey -eq 'Endpoint').OutputValue
$secretArn = ($outputs | Where-Object OutputKey -eq 'ClientTokenArn').OutputValue
$token = aws secretsmanager get-secret-value --secret-id $secretArn --query SecretString --output text @target
if ($LASTEXITCODE -ne 0) { throw 'Could not read the HazeGate token.' }
$headers = @{ Authorization = "Bearer $token" }
$status = Invoke-RestMethod -Uri $endpoint -Headers $headers
if ($status.service -ne 'HazeGate' -or $status.protocol -ne 1) { throw 'Unexpected HazeGate status.' }
[Environment]::SetEnvironmentVariable('HAZEGATE_ENDPOINT', $endpoint, 'User')
[Environment]::SetEnvironmentVariable('HAZEGATE_TOKEN', $token, 'User')
```

Restart PhantomHaze from the Windows Start menu and select **Settings → General → Web View protocol → HazeGate**. Other clients need the same two environment variables. Distribute the token privately. It grants access to the fetcher and its usage charges. AWS project credentials are only required for deployment and reading the secret.

## Verify the fetch path

In the same PowerShell session, this checks that API Gateway and Lambda can fetch a public page end to end.

```powershell
$page = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' -Body '{"v":1,"mode":"page","url":"https://example.com/","bundle":true}'
if ($page.status -ne 200 -or $page.text -notmatch 'Example Domain') { throw 'HazeGate page check failed.' }
```

To run the offline protocol and adapter tests, use Node.js 22 or newer and run `node --test` from this directory. The Lambda package uses only built-in Node modules.

The client caps uploads at 4 MiB of protocol JSON. Lambda also enforces a 6 MiB limit on its serialized synchronous request and response envelopes. HazeGate compresses larger replies when the client accepts gzip. If a page bundle exceeds the response limit, PhantomHaze retrieves its resources individually. A single resource that still exceeds the limit returns HTTP 413. Slow requests can time out at the gateway.
