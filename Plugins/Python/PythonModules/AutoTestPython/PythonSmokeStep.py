import OpenTap
from opentap import TestStep, attribute, property
from System import String


@attribute(OpenTap.Display("Python Smoke Step", "Verifies the official OpenTAP Python integration.", "Python"))
class PythonSmokeStep(TestStep):
    Message = property(String, "Python TestStep executed successfully")

    def __init__(self):
        super().__init__()

    def Run(self):
        super().Run()
        self.log.Info("{0}", self.Message)
        self.UpgradeVerdict(OpenTap.Verdict.Pass)