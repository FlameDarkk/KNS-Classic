.class public Ld;
.super Ljava/lang/Object;


# annotations
.annotation system Ldalvik/annotation/MemberClasses;
    value = {
        Ld$a;,
        Ld$b;
    }
.end annotation


# static fields
.field private static a:Ljava/util/HashMap;
    .annotation system Ldalvik/annotation/Signature;
        value = {
            "Ljava/util/HashMap",
            "<",
            "Ld$b;",
            "Ld$a;",
            ">;"
        }
    .end annotation
.end field

.field private static b:Ld$b;


# direct methods
.method static constructor <clinit>()V
    .locals 1

    new-instance v0, Ljava/util/HashMap;

    invoke-direct {v0}, Ljava/util/HashMap;-><init>()V

    sput-object v0, Ld;->a:Ljava/util/HashMap;

    sget-object v0, Ld$b;->a:Ld$b;

    sput-object v0, Ld;->b:Ld$b;

    return-void
.end method

.method public static a()V
    .locals 1

    sget-object v0, Ld;->a:Ljava/util/HashMap;

    invoke-virtual {v0}, Ljava/util/HashMap;->clear()V

    return-void
.end method

.method public static a(Ld$b;)V
    .locals 4

    new-instance v3, Ljava/lang/StringBuilder;

    invoke-direct {v3}, Ljava/lang/StringBuilder;-><init>()V

    const-string v2, "cmd ordinal="

    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;

    invoke-virtual {p0}, Ld$b;->ordinal()I

    move-result v2

    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;

    const-string v2, " name="

    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;

    invoke-virtual {p0}, Ld$b;->name()Ljava/lang/String;

    move-result-object v2

    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;

    invoke-virtual {v3}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;

    move-result-object v2

    const-string v3, "KNSPROBE"

    invoke-static {v3, v2}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I

    sput-object p0, Ld;->b:Ld$b;

    sget-object v0, Ld;->a:Ljava/util/HashMap;

    sget-object v1, Ld;->b:Ld$b;

    invoke-virtual {v0, v1}, Ljava/util/HashMap;->get(Ljava/lang/Object;)Ljava/lang/Object;

    move-result-object v0

    check-cast v0, Ld$a;

    invoke-virtual {v0}, Ld$a;->a()V

    return-void
.end method

.method public static b()Ld$b;
    .locals 1

    sget-object v0, Ld;->b:Ld$b;

    return-object v0
.end method

.method static synthetic c()Ljava/util/HashMap;
    .locals 1

    sget-object v0, Ld;->a:Ljava/util/HashMap;

    return-object v0
.end method
