from django.apps import AppConfig
from django.core.exceptions import ImproperlyConfigured
class TdwinConfig(AppConfig):
    name="tdwin"
    verbose_name="TD Booking Suite per Windows"
    default_auto_field="django.db.models.BigAutoField"
    def ready(self):
        """The installed program is compiled (windows/compile_tdwin.py): it does not start when the licence check
        was taken out of the settings or swapped with Python code."""
        from django.conf import settings
        from . import licensing,middleware,postazioni
        compiled=type(licensing.parse)
        if compiled.__name__!="cython_function_or_method":return  # the sources, while working on them
        checks=(licensing.parse,licensing.verifier,licensing.status,licensing.apply,licensing.clock,licensing.installed,licensing.problem,
                middleware.WindowsMiddleware.__call__,middleware.allowed_businesses,postazioni.limit,postazioni.current)
        if "tdwin.middleware.WindowsMiddleware" not in settings.MIDDLEWARE or any(type(f) is not compiled for f in checks):
            raise ImproperlyConfigured("TD Booking Suite è stato modificato: reinstalla il programma.")
