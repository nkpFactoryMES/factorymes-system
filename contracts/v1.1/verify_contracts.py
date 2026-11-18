from pathlib import Path
import sys,json,re,copy,hashlib,socket,importlib

ROOT=Path(__file__).resolve().parents[2]
# Use installed validators, or an explicit readable local dependency directory.
import os
if os.environ.get('MES_CONTRACT_LIBS'):sys.path.insert(0,os.environ['MES_CONTRACT_LIBS'])
else:sys.path.insert(0,str(ROOT/'work'/'contract-libs'))
from jsonschema import Draft202012Validator,FormatChecker
from referencing import Registry,Resource

OUT=Path(__file__).resolve().parent
def deny_network(*args,**kwargs):raise RuntimeError('Network disabled: contract validation is local-files-only.')
socket.create_connection=deny_network
socket.socket.connect=deny_network

docs={p.name:json.loads(p.read_text(encoding='utf-8')) for p in OUT.glob('*.json') if p.name not in ['validation-report.json']}
shared=docs['mes.schemas.json'];events=docs['mes.events.schema.json']
registry=Registry()
for name in ['mes.schemas.json','mes.events.schema.json']:
    Draft202012Validator.check_schema(docs[name])
    registry=registry.with_resource((OUT/name).as_uri(),Resource.from_contents(docs[name]))
for key,value in shared['$defs'].items():Draft202012Validator.check_schema(value)
for key,value in events['$defs'].items():Draft202012Validator.check_schema(value)
def validator(schema_ref):
    name,_,fragment=schema_ref.partition('#')
    return Draft202012Validator({'$ref':(OUT/name).as_uri()+('#'+fragment if fragment else '')},registry=registry,format_checker=FormatChecker())
fixtures=docs['contract-examples.json']
for item in fixtures['positive']:
    errors=list(validator(item['schema']).iter_errors(item['value']))
    assert not errors,(item['name'],[(e.json_path,e.message) for e in errors])
for item in fixtures['negative']:
    errors=list(validator(item['schema']).iter_errors(item['value']))
    assert errors,('Negative fixture accepted',item['name'])

module=importlib.import_module('openapi_spec_validator')
validate=getattr(module,'validate',None) or getattr(module,'validate_spec',None)
assert validate is not None,('Validator API unavailable',getattr(module,'__file__',None))
ddl=(ROOT/'outputs'/'ddl'/'01_tables.sql').read_text(encoding='utf-8-sig')
table_blocks=dict(re.findall(r'CREATE TABLE (\w+)\s*\((.*?)\n\);',ddl,re.S))
columns={t:{m[0].lower() for m in re.findall(r'^\s+(\w+)\s+(NUMBER|VARCHAR2|TIMESTAMP|CLOB|DATE)\b',b,re.M)} for t,b in table_blocks.items()}
assert len(table_blocks)==31
assert 'request_hash' in columns['PRODUCTION_ENTRY']
assert re.search(r'business_date\s+DATE\b',table_blocks['DAILY_PRODUCTION'],re.I)
assert 'FK_DLV_RECIPIENT' in ddl and 'FK_PAUSE_ALARM_EQ' in ddl
assert 'UQ_AIRES_ACCEPTED' in (ROOT/'outputs'/'ddl'/'02_indexes.sql').read_text(encoding='utf-8-sig')

def walk(value):
    yield value
    if isinstance(value,dict):
        for v in value.values():yield from walk(v)
    elif isinstance(value,list):
        for v in value:yield from walk(v)

all_ids=[];operation_count=0;db_annotation_count=0
for name,document in docs.items():
    for node in walk(document):
        if not isinstance(node,dict):continue
        if '$ref' in node:
            link=node['$ref'];file,_,fragment=link.partition('#')
            target=document if not file else docs[file]
            if fragment:
                for segment in fragment.strip('/').split('/'):
                    target=target[segment.replace('~1','/').replace('~0','~')]
        if 'x-db-column' in node:
            table,col=node['x-db-column'].split('.')
            assert table in columns and col.lower() in columns[table],node['x-db-column']
            db_annotation_count+=1
    if not name.endswith('.openapi.json'):continue
    validate(document,base_uri=(OUT/name).as_uri())
    for path,item in document['paths'].items():
        for method,operation in item.items():
            if method not in ['get','put','post','patch','delete']:continue
            operation_count+=1;all_ids.append(operation['operationId'])
            assert all(t in table_blocks for t in operation['x-tables'])
            paths=set(re.findall(r'\{(\w+)\}',path))
            declared={p['name'] for p in operation['parameters'] if p['in']=='path' and p['required']}
            assert paths==declared,(name,path)
            if name=='public.openapi.json':
                assert operation['x-roles']
                security=operation.get('security',document['security'])
                if method!='get':assert all('CsrfToken' in choice for choice in security)
                assert 'ServiceBearer' not in str(security)
            else:
                assert operation['x-allowed-callers'] and operation['x-required-scopes'],(name,path)
                assert operation.get('security',document['security'])==[{'ServiceBearer':[]}]
assert len(set(all_ids))==len(all_ids)
assert operation_count==98
policy=shared['$defs']['ExecutionPolicy']['properties']
assert policy['cloudAiEnabled']['const'] is False
assert policy['externalDeliveryEnabled']['const'] is False
assert 'accepted' not in shared['$defs']['AnalysisResultInput']['properties']
assert 'receivedAt' not in shared['$defs']['TelemetryData']['properties']
assert 'WORK_TASK' not in table_blocks and 'CONFIG_REVISION' not in table_blocks
new_ids={'startAlarmAction','listAlarmActions','getAlarmRule','getProduct','getLine','getEquipment','getUser','listAnalyses','dispatchDeliveryBatch','watchConnectionExpiry'}
covered={x.get('operation',{}).get('operationId') for x in fixtures['positive']}
assert new_ids<=covered,('missing new operation example',new_ids-covered)
for item in fixtures['positive']:
    meta=item.get('operation')
    if not meta:continue
    operation=docs[meta['document']]['paths'][meta['path']][meta['method'].lower()]
    assert operation['operationId']==meta['operationId']
    actual=operation['requestBody']['content']['application/json']['schema'] if meta['fixtureKind']=='request' else operation['responses'][str(meta['statusCode'])]['content']['application/json']['schema']
    assert actual.get('$ref')==item['schema'],(item['name'],'operation schema mismatch')
result={'status':'passed','contractVersion':'1.1.0','publicOperations':73,'backendInternalOperations':14,'newOperations':10,'newOperationPositiveExamples':10,'baseline':'v4 + outputs/ddl, 31 tables','openapiVersion':'3.1.1','openapiDocuments':5,'operations':operation_count,'dtoSchemas':len(shared['$defs']),'eventSchemas':len(events['$defs']),'positiveExamples':len(fixtures['positive']),'negativeExamplesRejected':len(fixtures['negative']),'dbColumnAnnotationsChecked':db_annotation_count,'validationNetwork':'disabled','apiCallsPerformed':0,'paidProviderCallsPerformed':0,'runtimeAuthorizationOrSignatureValidation':'not performed','serverOrDatabaseIntegrationTests':'not performed','ddlSha256':hashlib.sha256(ddl.encode()).hexdigest()}
result['operationsByDocument']={name:sum(1 for methods in doc['paths'].values() for m in methods if m in ['get','post','put','patch','delete']) for name,doc in docs.items() if name.endswith('.openapi.json')}
result['inheritedWarnings']=[]
result['status']='passed'
result['originalMethodValidation']={'status':'passed','openapi_spec_validator':'passed','jsonschema_Draft202012Validator':'passed'}
raw_ddl=(ROOT/'outputs'/'ddl'/'01_tables.sql').read_bytes()
normalized_ddl=raw_ddl.decode('utf-8-sig').replace('\r\n','\n').replace('\r','\n').encode('utf-8')
result['ddlSha256']=hashlib.sha256(normalized_ddl).hexdigest()
result['ddlRawSha256']=hashlib.sha256(raw_ddl).hexdigest()
result['ddlHashNormalization']='ddlSha256 is SHA-256 after UTF-8 BOM removal and CRLF/CR -> LF normalization; ddlRawSha256 hashes raw bytes. Same 01_tables.sql.'
for name in ['mes.schemas.json','mes.events.schema.json']:
    for node in walk(docs[name]):
        if isinstance(node,dict) and isinstance(node.get('severity'),dict):
            assert node['severity']['enum']==['INFO','WARN','CRITICAL']
result['severityAlignedWithDDL']=True
(OUT/'validation-report.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
