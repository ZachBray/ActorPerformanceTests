package ag.bett.scala.test


case object GetAndReset
case class AddCount(number:Long)


object BenchmarkAll extends App {

	override def main(args: Array[String]) {

		// lift
		println("Warmup run!")
		akka.Application.start()
		println("Warmup run finished!")
		val runtime = Runtime.getRuntime

		println("Garbage Collection")
		runtime.gc
		println

		// akka
		akka.Application.start()
		akka.Application.stop()

		sys.exit(0)
	}
}

