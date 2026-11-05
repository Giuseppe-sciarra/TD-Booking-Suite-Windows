from django.urls import path
from tdbooking.urls import urlpatterns as core
from . import views,postazioni,backup_views,rete_views,stato
urlpatterns=[
 path("primo-avvio/",views.first_run,name="first_run"),
 path("platform/licenza/",views.licence_view,name="licence"),
 path("platform/inizia/",views.guide,name="setup_guide"),
 path("platform/inizia/esci/",views.leave_as,name="leave_as"),
 path("postazione/",postazioni.pair_view,name="pair"),
 path("postazione/certificato.crt",postazioni.ca_view,name="ca_certificate"),
 path("platform/postazioni/",postazioni.admin_view,name="workstations"),
 path("platform/backup/",backup_views.page,name="backup"),
 path("platform/rete/",rete_views.page,name="network"),
 path("platform/stato/",stato.page,name="program_state"),
 path("platform/stato/lento/",stato.slow,name="program_slow"),
 path("platform/backup/scarica/<str:name>",backup_views.download,name="backup_download"),
]+core
