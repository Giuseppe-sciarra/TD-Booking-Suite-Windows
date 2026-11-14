"""3.0.3: the SQL each Django migration really runs (protections included), for an older 2.x database the C# opens.
Migrates an empty database and records, per migration, the statements it sends. Only migrations without data steps:
core from 0030 on (before, 0023/0027/0029 change rows) and every tdwin one.
Run from the Windows repo on an EMPTY database:
PYTHONPATH=.:suite DJANGO_SETTINGS_MODULE=tdwin.test_settings DATABASE_URL=postgres://.../tdgen python net/tools/gen_catchup.py > net/src/TD.Core/Sql/catchup.sql"""
import sys
import django;django.setup()
from django.db import connection
from django.db.migrations.executor import MigrationExecutor

FROM={"core":"0030","tdwin":"0000"}
out=[];current=[None]

def keep(app,name):return app in FROM and name>=FROM[app]

def wrapper(execute,sql,params,many,context):
    if current[0] and not many and not sql.lstrip().upper().startswith("SELECT") and "django_migrations" not in sql:
        raw=context["cursor"].cursor
        out[-1][1].append(raw.mogrify(sql,params) if params else sql)
    return execute(sql,params,many,context)

def progress(action,migration=None,fake=False):
    if action=="apply_start":
        current[0]=keep(migration.app_label,migration.name)
        if current[0]:out.append((f"{migration.app_label} {migration.name}",[]))
    elif action=="apply_success":current[0]=None

ex=MigrationExecutor(connection,progress)
if ex.loader.applied_migrations:sys.exit("Serve un database vuoto")
with connection.execute_wrapper(wrapper):ex.migrate(ex.loader.graph.leaf_nodes())
for name,stmts in out:
    print(f"-- migration: {name}")
    for s in stmts:print(s.rstrip().rstrip(";")+";")
