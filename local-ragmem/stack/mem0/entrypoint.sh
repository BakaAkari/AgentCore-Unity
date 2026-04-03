#!/bin/bash
set -e

# Install ollama Python client
pip install --quiet ollama

# Patch mem0: remove unsupported 'store' parameter for non-OpenAI providers
python3 -c "
f = '/usr/local/lib/python3.12/site-packages/mem0/llms/openai.py'
with open(f) as fh:
    t = fh.read()
t = t.replace('openai_specific_generation_params = [\"store\"]', 'openai_specific_generation_params = []')
with open(f, 'w') as fh:
    fh.write(t)
print('Patched: removed store param from openai.py')
"

# Patch mem0: remove top_p (conflicts with temperature on Anthropic)
python3 -c "
f = '/usr/local/lib/python3.12/site-packages/mem0/llms/base.py'
with open(f) as fh:
    t = fh.read()
t = t.replace('\"top_p\": self.config.top_p,', '# \"top_p\": removed for Anthropic compatibility')
with open(f, 'w') as fh:
    fh.write(t)
print('Patched: removed top_p from base.py')
"

# Start mem0 server
exec uvicorn main:app --host 0.0.0.0 --port 8000
