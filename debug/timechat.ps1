$baseUrl = "http://localhost:5200"

Write-Host "=== Simple chat (warm) ==="
$body1 = '{"model":"phi-4-mini","messages":[{"role":"user","content":"hi"}]}'
$r1 = Measure-Command { $out1 = Invoke-RestMethod -Uri "$baseUrl/chat/completions" -Method Post -ContentType "application/json" -Body $body1 }
Write-Host "Response: $($out1.choices[0].message.content)"
Write-Host "Tokens: prompt=$($out1.usage.prompt_tokens) completion=$($out1.usage.completion_tokens)"
Write-Host ("Elapsed: {0:N0}ms" -f $r1.TotalMilliseconds)

Write-Host ""
Write-Host "=== Chat with tools + system prompt (like webapp) ==="
$body2 = @'
{"model":"phi-4-mini","messages":[{"role":"system","content":"You are an AI customer service agent for AdventureWorks. You try to be concise."},{"role":"assistant","content":"Hi! How can I help?"},{"role":"user","content":"show me red shoes"}],"tools":[{"type":"function","function":{"name":"SearchCatalog","description":"Searches the catalog","parameters":{"type":"object","properties":{"productDescription":{"type":"string"}},"required":["productDescription"]}}}],"max_tokens":512}
'@
$r2 = Measure-Command { $out2 = Invoke-RestMethod -Uri "$baseUrl/chat/completions" -Method Post -ContentType "application/json" -Body $body2 }
Write-Host "Response: $(ConvertTo-Json $out2.choices[0].message -Compress)"
Write-Host "Tokens: prompt=$($out2.usage.prompt_tokens) completion=$($out2.usage.completion_tokens)"
Write-Host ("Elapsed: {0:N0}ms" -f $r2.TotalMilliseconds)

Write-Host ""
Write-Host "=== Embeddings ==="
$body3 = '{"input":"red shoes","model":"all-MiniLM-L6-v2"}'
$r3 = Measure-Command { $out3 = Invoke-RestMethod -Uri "$baseUrl/v1/embeddings" -Method Post -ContentType "application/json" -Body $body3 }
Write-Host "Embedding dims: $($out3.data[0].embedding.Count)"
Write-Host ("Elapsed: {0:N0}ms" -f $r3.TotalMilliseconds)
